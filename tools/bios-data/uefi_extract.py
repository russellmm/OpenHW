"""Low-level helpers for pulling setup-menu data out of an AMI Aptio V BIOS capsule.

- walk firmware volumes / FFS files / sections (LZMA-compressed ones too)
- parse HII string packages (menu text)
- parse IFR form packages (menu layout, options, defaults, show/hide conditions)
- parse AMI NVAR variable stores (factory defaults: StdDefaults)

Only the Python standard library is used.
"""
import lzma
import re
import struct
import uuid

LZMA_GUIDS = {uuid.UUID('EE4E5898-3914-4259-9D6E-DC7BD79403CF'),
              uuid.UUID('D42AE6BD-1352-4BFB-909A-CA72A6EAE889')}
FFS_GUIDS = {uuid.UUID('8C8CE578-8A3D-4F1C-9935-896185C32DD3'),
             uuid.UUID('5473C07A-3DCB-4DCA-BD6F-1E9689E7349A')}


def u16(b, o): return struct.unpack_from('<H', b, o)[0]
def u32(b, o): return struct.unpack_from('<I', b, o)[0]
def guid_at(b, o): return str(uuid.UUID(bytes_le=bytes(b[o:o + 16])))


# --------------------------------------------------------------------------- firmware volumes
def _lzma(b):
    b = bytes(b)
    hdr = b[:5] + struct.pack('<Q', 0xFFFFFFFFFFFFFFFF)
    return lzma.LZMADecompressor(format=lzma.FORMAT_ALONE).decompress(hdr + b[13:])


def _sections(b, fguid, out):
    o = 0
    while o + 4 <= len(b):
        size = b[o] | (b[o + 1] << 8) | (b[o + 2] << 16)
        typ = b[o + 3]
        hl = 4
        if size == 0xFFFFFF:
            size = u32(b, o + 4); hl = 8
        if size < hl or o + size > len(b):
            break
        body = b[o + hl:o + size]
        if typ == 0x02:  # GUID-defined (usually LZMA)
            sg = uuid.UUID(bytes_le=bytes(body[:16]))
            data = body[u16(body, 16) - hl:]
            if sg in LZMA_GUIDS:
                try:
                    _sections(_lzma(data), fguid, out)
                except Exception:
                    pass
            else:
                _sections(data, fguid, out)
        elif typ == 0x01 and body[4] == 0:  # uncompressed compression section
            _sections(body[5:], fguid, out)
        elif typ == 0x17:  # nested firmware volume
            _fv(body, out)
        else:
            out.append((fguid, typ, bytes(body)))
        o += (size + 3) & ~3


def _fv(b, out):
    if len(b) < 0x40 or bytes(b[40:44]) != b'_FVH':
        return
    hlen = u16(b, 48)
    fvlen = struct.unpack_from('<Q', b, 32)[0]
    ext = u16(b, 52)
    o = hlen
    if ext:
        o = ext + u32(b, ext + 16)
    o = (o + 7) & ~7
    end = min(fvlen, len(b))
    while o + 24 <= end:
        if bytes(b[o:o + 16]) == b'\xff' * 16:
            break
        fg = guid_at(b, o)
        ftype = b[o + 18]; attr = b[o + 19]
        size = b[o + 20] | (b[o + 21] << 8) | (b[o + 22] << 16)
        hl = 24
        if attr & 0x01 and size == 0:
            size = struct.unpack_from('<Q', b, o + 24)[0]; hl = 32
        if size < hl:
            break
        body = b[o + hl:o + size]
        if ftype not in (0xF0, 0x01):
            _sections(body, fg, out)
        o = (o + size + 7) & ~7


def walk_capsule(data):
    """Return list of (file_guid, section_type, bytes) for every leaf section."""
    out = []
    seen = []
    for m in re.finditer(b'_FVH', data):
        off = m.start() - 40
        if off < 0:
            continue
        fvlen = struct.unpack_from('<Q', data, off + 32)[0]
        if fvlen > len(data) or fvlen < 0x48:
            continue
        if uuid.UUID(bytes_le=data[off + 16:off + 32]) not in FFS_GUIDS:
            continue
        if any(s <= off < e for s, e in seen):
            continue
        seen.append((off, off + fvlen))
        _fv(memoryview(data)[off:off + fvlen], out)
    return out


# --------------------------------------------------------------------------- HII strings
def find_string_packages(b, lang='en-US'):
    res = []
    for m in re.finditer(re.escape(lang.encode()) + b'\x00', b):
        o = m.start() - 0x2e
        if o >= 0 and b[o + 3] == 4 and u32(b, o + 4) >= 0x2e:
            try:
                res.append(parse_strings(b, o))
            except Exception:
                pass
    return res


def parse_strings(b, o):
    ln = b[o] | b[o + 1] << 8 | b[o + 2] << 16
    info = u32(b, o + 8)
    p = o + info; end = o + ln; sid = 1; strs = {}

    def ucs2(p):
        e = p
        while b[e] | b[e + 1]:
            e += 2
        return bytes(b[p:e]).decode('utf-16le', 'replace'), e + 2

    def ascii_(p):
        e = bytes(b[p:end]).index(b'\0')
        return bytes(b[p:p + e]).decode('latin1'), p + e + 1

    while p < end:
        t = b[p]
        if t == 0x00:
            break
        elif t == 0x14:
            strs[sid], p = ucs2(p + 1); sid += 1
        elif t == 0x15:
            strs[sid], p = ucs2(p + 2); sid += 1
        elif t in (0x16, 0x17):
            n = u16(b, p + (1 if t == 0x16 else 2)); p += 3 if t == 0x16 else 4
            for _ in range(n):
                strs[sid], p = ucs2(p); sid += 1
        elif t in (0x10, 0x11):
            strs[sid], p = ascii_(p + (1 if t == 0x10 else 2)); sid += 1
        elif t in (0x12, 0x13):
            p += 1 if t == 0x12 else 2
            n = u16(b, p); p += 2
            for _ in range(n):
                strs[sid], p = ascii_(p); sid += 1
        elif t == 0x20:
            strs[sid] = strs.get(u16(b, p + 1), ''); sid += 1; p += 3
        elif t == 0x21:
            sid += u16(b, p + 1); p += 3
        elif t == 0x22:
            sid += b[p + 1]; p += 2
        elif t == 0x30:
            p += b[p + 2]
        elif t == 0x31:
            p += u16(b, p + 2)
        elif t == 0x32:
            p += u32(b, p + 2)
        elif t == 0x40:
            p += u16(b, p + 1)
        else:
            raise ValueError('unknown string block 0x%x' % t)
    return strs


# --------------------------------------------------------------------------- IFR
# opcodes that belong to expressions (conditions)
EXPR_OPS = (set(range(0x12, 0x18)) | {0x20, 0x21, 0x22, 0x2A, 0x2B, 0x2D}
            | set(range(0x2F, 0x5A)) | {0x5E, 0x64})
EXPR_NAMES = {0x15: 'and', 0x16: 'or', 0x17: 'not', 0x2F: 'eq', 0x30: 'ne', 0x31: 'gt', 0x32: 'ge',
              0x33: 'lt', 0x34: 'le', 0x35: 'band', 0x36: 'bor', 0x37: 'bnot', 0x38: 'shl',
              0x39: 'shr', 0x3A: 'add', 0x3B: 'sub', 0x3C: 'mul', 0x3D: 'div', 0x3E: 'mod',
              0x46: 'true', 0x47: 'false', 0x52: 'zero', 0x53: 'one', 0x54: 'ones',
              0x48: 'touint', 0x4A: 'tobool', 0x50: 'cond', 0x58: 'this', 0x57: 'dup'}
COND_KIND = {0x0A: 'hide', 0x19: 'gray', 0x1E: 'hide'}   # suppressif / grayoutif / disableif


def _val(b, o, typ):
    if typ == 0: return b[o]
    if typ == 1: return u16(b, o)
    if typ == 2: return u32(b, o)
    if typ == 3: return struct.unpack_from('<Q', b, o)[0]
    if typ == 4: return int(bool(b[o]))
    return None


def find_form_packages(b):
    res = []
    for m in re.finditer(rb'\x02\x0e[\xa0-\xff]', b):
        o = m.start() - 3
        if o < 0:
            continue
        ln = b[o] | b[o + 1] << 8 | b[o + 2] << 16
        if 40 < ln <= len(b) - o:
            res.append(o)
    return res


def parse_forms(b, o, S):
    """Parse a forms package. Returns list of formset dicts."""
    ln = b[o] | b[o + 1] << 8 | b[o + 2] << 16
    p = o + 4; end = o + ln
    S = S or {}
    s = lambda i: S.get(i, '')
    formsets = []
    stack = []       # (kind, obj)
    cond = []        # active conditions [(kind, rpn)]
    pending = None   # condition whose expression is being read
    cur_fs = cur_form = None
    last_q = None
    while p < end:
        op = b[p]; l = b[p + 1] & 0x7F; scope = b[p + 1] >> 7
        if l < 2:
            break
        body = b[p:p + l]
        if pending is not None and op == 0x29 and stack and stack[-1][0] == 'expr':
            stack.pop(); p += l
            continue
        if pending is not None and op not in EXPR_OPS:
            cond.append(pending); stack[-1] = ('cond', None); pending = None
        kind = None; obj = None
        if pending is not None and op in EXPR_OPS:
            r = pending[1]
            if op == 0x12: r.append(['eqv', u16(body, 2), u16(body, 4)])
            elif op == 0x13: r.append(['eqid', u16(body, 2), u16(body, 4)])
            elif op == 0x14: r.append(['eqlist', u16(body, 2), [u16(body, 6 + 2 * i) for i in range(u16(body, 4))]])
            elif op == 0x40: r.append(['qref', u16(body, 2)])
            elif op in (0x42, 0x43, 0x44, 0x45): r.append(['const', _val(body, 2, op - 0x42)])
            elif op in EXPR_NAMES: r.append([EXPR_NAMES[op]])
            else: r.append(['unknown', op])
            kind = 'expr'
        elif op == 0x0E:  # FORM_SET
            cur_fs = {'guid': guid_at(body, 2), 'title': s(u16(body, 18)), 'help': s(u16(body, 20)),
                      'forms': [], 'varstores': {}}
            formsets.append(cur_fs); kind = 'formset'
        elif op == 0x01 and cur_fs is not None:  # FORM
            cur_form = {'id': u16(body, 2), 'title': s(u16(body, 4)), 'items': []}
            cur_fs['forms'].append(cur_form); kind = 'form'
        elif op in COND_KIND:
            pending = [COND_KIND[op], []]; kind = 'pendcond'
        elif op in (0x24, 0x25, 0x26) and cur_fs is not None:  # varstores
            if op == 0x24:
                g = guid_at(body, 2); vid = u16(body, 18); sz = u16(body, 20)
                name = bytes(body[22:]).split(b'\0')[0].decode('latin1')
            elif op == 0x26:
                vid = u16(body, 2); g = guid_at(body, 4); sz = u16(body, 24)
                name = bytes(body[26:]).split(b'\0')[0].decode('latin1')
            else:
                vid = u16(body, 2); g = guid_at(body, 4); sz = 0; name = 'NameValue'
            cur_fs['varstores'][vid] = {'guid': g, 'name': name, 'size': sz}
        elif op in (0x02, 0x03):  # SUBTITLE / TEXT
            obj = {'t': 'sub' if op == 0x02 else 'text', 'p': s(u16(body, 2)), 'h': s(u16(body, 4))}
            if op == 0x03:
                obj['v'] = s(u16(body, 6))
        elif op in (0x05, 0x06, 0x07, 0x08, 0x0C, 0x0F, 0x1A, 0x1B, 0x1C, 0x23):
            q = {'p': s(u16(body, 2)), 'h': s(u16(body, 4)), 'q': u16(body, 6),
                 'vs': u16(body, 8), 'o': u16(body, 10), 'qf': body[12]}
            if op in (0x05, 0x07):
                fl = body[13]; sz = fl & 0x0F; w = 1 << sz
                q['t'] = 'oneof' if op == 0x05 else 'num'
                q['sz'] = w
                q['min'] = _val(body, 14, sz); q['max'] = _val(body, 14 + w, sz)
                q['step'] = _val(body, 14 + 2 * w, sz)
                if op == 0x07:
                    q['disp'] = (fl >> 4) & 3
                else:
                    q['opts'] = []
            elif op == 0x06:
                q['t'] = 'check'; q['sz'] = 1; q['def'] = 1 if body[13] & 1 else 0
            elif op == 0x08:
                q['t'] = 'password'; q['minl'] = u16(body, 13); q['maxl'] = u16(body, 15)
            elif op == 0x0C:
                q['t'] = 'action'
            elif op == 0x0F:
                q['t'] = 'ref'
                if l >= 15: q['f'] = u16(body, 13)
                if l >= 33: q['fsguid'] = guid_at(body, 17)
            elif op == 0x1A:
                q['t'] = 'date'
            elif op == 0x1B:
                q['t'] = 'time'
            elif op == 0x1C:
                q['t'] = 'string'; q['minl'] = body[13]; q['maxl'] = body[14]; q['sz'] = body[14] * 2
            elif op == 0x23:
                q['t'] = 'olist'; q['maxc'] = body[13]; q['opts'] = []
            obj = q; last_q = q
            q['_nc'] = len(cond)
        elif op == 0x09 and last_q is not None and 'opts' in last_q:  # ONE_OF_OPTION
            fl = body[4]; typ = body[5]
            opt = [s(u16(body, 2)), _val(body, 6, typ)]
            extra = cond[last_q['_nc']:]
            if fl & 0x10:
                last_q.setdefault('ifrdef', opt[1])
            if extra:
                opt.append([list(c) for c in extra])
            last_q['opts'].append(opt)
        elif op == 0x5B and last_q is not None:  # DEFAULT
            if u16(body, 2) == 0 and body[4] < 5:
                last_q['ifrdef'] = _val(body, 5, body[4])
        if obj is not None and cur_form is not None:
            if cond:
                obj['c'] = [list(c) for c in cond]
            cur_form['items'].append(obj)
            kind = 'q'
        if op == 0x29:
            if stack:
                k, _ = stack.pop()
                if k == 'cond':
                    cond.pop()
                elif k == 'form':
                    cur_form = None
                elif k == 'q':
                    last_q = None
        elif scope:
            stack.append((kind or 'other', obj))
        p += l
    for fs in formsets:
        for f in fs['forms']:
            for it in f['items']:
                it.pop('_nc', None)
    return formsets


# --------------------------------------------------------------------------- AMI NVAR
def parse_nvar(d, start, end, guidstore_end):
    out = []
    p = start
    while p + 10 <= end and d[p:p + 4] == b'NVAR':
        size = u16(d, p + 4); at = d[p + 9]
        if size < 10 or size == 0xFFFF:
            break
        body = d[p + 10:p + size]
        if at & 0x10:
            ext = u16(body, len(body) - 2)
            if ext <= len(body):
                body = body[:-ext]
        if not at & 0x08:
            q = 16 if at & 0x04 else 1
            if at & 0x02:
                e = body.index(b'\0', q); name = body[q:e].decode('latin1'); q = e + 1
            else:
                e = q
                while body[e] | body[e + 1]:
                    e += 2
                name = body[q:e].decode('utf-16le'); q = e + 2
            out.append({'off': p, 'name': name, 'data': bytes(body[q:]), 'at': at, 'hdr': q})
        p += size
    return out


def std_defaults(data):
    """Return {var_name: bytes} from the AMI 'StdDefaults' store in the capsule."""
    i = data.find(b'StdDefaults\x00')
    if i < 0:
        return {}
    # walk back to the owning NVAR header
    p = data.rfind(b'NVAR', 0, i)
    top = parse_nvar(data, p, p + u16(data, p + 4), 0)
    if not top or top[0]['name'] != 'StdDefaults':
        return {}
    e = top[0]
    s0 = p + 10 + e['hdr']
    inner = parse_nvar(data, s0, s0 + len(e['data']), 0)
    return {x['name']: x['data'] for x in inner}
