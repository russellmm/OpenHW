set -e
CC="$(cd "$(dirname "$0")" && pwd)/usr/bin/pawncc"
cd "$1"; "$CC" "$2" -iinclude -C64 '-;+' '-(+' -p -o"$3"
