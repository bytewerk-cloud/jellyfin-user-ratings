#!/usr/bin/env bash
# Dev helper: authenticated call to the local test server.
# usage: docker/jf.sh <user> <pass> <METHOD> <path> [json-body]
B=${JF_URL:-http://localhost:8096}
AUTH='MediaBrowser Client="ducks-dev", Device="cli", DeviceId="ducks-cli-'$1'", Version="0.1"'
TOKEN=$(curl -s -X POST "$B/Users/AuthenticateByName" -H 'Content-Type: application/json' -H "Authorization: $AUTH" \
  -d "{\"Username\":\"$1\",\"Pw\":\"$2\"}" | python3 -c "import sys,json;print(json.load(sys.stdin)['AccessToken'])")
ARGS=(-s -w "\n[%{http_code}]\n" -X "$3" "$B$4" -H "Authorization: $AUTH, Token=\"$TOKEN\"")
[ -n "${5:-}" ] && ARGS+=(-H 'Content-Type: application/json' -d "$5")
curl "${ARGS[@]}"
