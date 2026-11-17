#!/bin/bash
# Does a BMC login work over Redfish? Reads the user (line 1) and password (line 2) from a file, opens a Redfish
# session (POST SessionService/Sessions: 201 = accepted, 401 = refused), then closes it. Prints neither the password
# nor the session token. Reads alone prove nothing on this board: over the host interface they need no login.
#   tools/bmc-probe/login-test.sh /mnt/SOLIDIGM/Claude/.secrets/BMC_openhw [https://169.254.0.17] [enx36dbecc2f9a6]
set -u
FILE=${1:?usage: login-test.sh <credentials file: user on line 1, password on line 2> [bmc url] [interface]}
BMC=${2:-https://169.254.0.17}
IFACE=${3:-enx36dbecc2f9a6}
USER_NAME=$(sed -n 1p "$FILE" | tr -d '\r\n')
PASSWORD=$(sed -n 2p "$FILE" | tr -d '\r\n')
[ -n "$USER_NAME" ] && [ -n "$PASSWORD" ] || { echo "Expected the user on line 1 and the password on line 2 of $FILE"; exit 2; }

BODY=$(python3 -c 'import json,sys; print(json.dumps({"UserName": sys.argv[1], "Password": sys.argv[2]}))' "$USER_NAME" "$PASSWORD")
HEADERS=$(mktemp); trap 'rm -f "$HEADERS"' EXIT
CODE=$(curl -sk --max-time 10 --interface "$IFACE" -D "$HEADERS" -o /dev/null -w '%{http_code}' \
    -H 'Content-Type: application/json' --data @- "$BMC/redfish/v1/SessionService/Sessions" <<< "$BODY")
unset BODY PASSWORD

case "$CODE" in
    201|200) echo "Login as '$USER_NAME': accepted ($CODE)";;
    401) echo "Login as '$USER_NAME': refused (401) — wrong password, or the account can't use Redfish"; exit 1;;
    *) echo "Login as '$USER_NAME': unexpected reply $CODE"; exit 1;;
esac

# Close the session again (Location + X-Auth-Token from the reply headers; neither is printed).
LOCATION=$(grep -i '^Location:' "$HEADERS" | head -1 | cut -d' ' -f2- | tr -d '\r')
TOKEN=$(grep -i '^X-Auth-Token:' "$HEADERS" | head -1 | cut -d' ' -f2- | tr -d '\r')
if [ -n "$LOCATION" ] && [ -n "$TOKEN" ]; then
    case "$LOCATION" in http*) URL=$LOCATION;; *) URL=$BMC$LOCATION;; esac
    CLOSE=$(curl -sk --max-time 10 --interface "$IFACE" -o /dev/null -w '%{http_code}' -X DELETE -H "X-Auth-Token: $TOKEN" "$URL")
    echo "Session closed ($CLOSE)"
fi
