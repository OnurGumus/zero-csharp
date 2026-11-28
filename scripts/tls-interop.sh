#!/usr/bin/env bash
# Runs samples/Https.cs against OpenSSL's s_server on this machine, once per
# case: each cipher suite, RSA and ECDSA certificates (P-256 and P-384), a
# server that asks for a client certificate, and the cases that must fail
# (TLS 1.2 only, a P-256 key share only, a wrong host name, an unknown root).
#
# Usage:  scripts/tls-interop.sh [--mac | other ./run.sh target flags]
# Needs:  OpenSSL 3.0 or newer (CI uses Ubuntu 24.04's), and the Https sample built for the target
#         (./build.sh [--mac] Https). Ports 44330 and up on 127.0.0.1.
set -uo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
work=$(mktemp -d)
trap 'kill $(jobs -p) 2>/dev/null; rm -rf "$work"' EXIT
cd "$work"

# A root, and leaf certificates for "localhost" with each key type.
openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:prime256v1 -out root.key 2>/dev/null
openssl req -new -x509 -key root.key -subj "/CN=Interop Root" -days 30 -out root.pem \
    -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign" 2>/dev/null
printf '%s\n' "[x]" "basicConstraints=critical,CA:FALSE" "extendedKeyUsage=serverAuth" \
    "subjectAltName=DNS:localhost,IP:127.0.0.1" > leaf.ext
leaf() { # name, genpkey arguments
    local name=$1; shift
    openssl genpkey "$@" -out "$name.key" 2>/dev/null
    openssl req -new -key "$name.key" -subj "/CN=localhost" -out "$name.csr" 2>/dev/null
    openssl x509 -req -in "$name.csr" -CA root.pem -CAkey root.key -days 30 -extfile leaf.ext -extensions x \
        -out "$name.pem" 2>/dev/null
}
leaf p256 -algorithm EC -pkeyopt ec_paramgen_curve:prime256v1
leaf p384 -algorithm EC -pkeyopt ec_paramgen_curve:secp384r1
leaf rsa -algorithm RSA -pkeyopt rsa_keygen_bits:2048
openssl req -new -x509 -key p256.key -subj "/CN=localhost" -days 30 -out self.pem 2>/dev/null
# For the name check: a certificate for "localhost" only, reached as 127.0.0.1.
printf '%s\n' "[x]" "basicConstraints=critical,CA:FALSE" "subjectAltName=DNS:localhost" > leaf.ext
leaf named -algorithm EC -pkeyopt ec_paramgen_curve:prime256v1

export SSL_CERT_FILE=$work/root.pem
port=44330
pass=0
fail=0

# case name, expect (ok|fail), client host, then s_server arguments
check() {
    local name=$1 expect=$2 host=$3; shift 3
    port=$((port + 1))
    openssl s_server -accept "127.0.0.1:$port" -www -quiet "$@" >server.log 2>&1 &
    local server=$!
    for _ in $(seq 50); do
        (echo > /dev/tcp/127.0.0.1/$port) 2>/dev/null && break
        sleep 0.1
    done
    out=$(cd "$root" && timeout 30 ./run.sh "${TARGET[@]}" Https "$host:$port" / </dev/null 2>&1)
    code=$?
    kill $server 2>/dev/null
    wait $server 2>/dev/null
    local got=fail
    [ $code -eq 0 ] && printf '%s' "$out" | grep -q "200 ok" && got=ok
    if [ "$got" = "$expect" ]; then
        pass=$((pass + 1))
        echo "ok   $name"
    else
        fail=$((fail + 1))
        echo "FAIL $name (expected $expect)"
        printf '%s\n' "$out" | sed 's/^/     /' | tail -5
    fi
    if [ "$expect" = fail ]; then
        printf '%s\n' "$out" | head -1 | sed 's/^/     -> /'
    fi
}

TARGET=("$@")
check "AES-128-GCM"            ok   localhost -cert p256.pem -key p256.key -ciphersuites TLS_AES_128_GCM_SHA256
check "AES-256-GCM"            ok   localhost -cert p256.pem -key p256.key -ciphersuites TLS_AES_256_GCM_SHA384
check "ChaCha20-Poly1305"      ok   localhost -cert p256.pem -key p256.key -ciphersuites TLS_CHACHA20_POLY1305_SHA256
check "P-384 certificate"      ok   localhost -cert p384.pem -key p384.key
check "RSA certificate (PSS)"  ok   localhost -cert rsa.pem -key rsa.key
check "IP address"             ok   127.0.0.1 -cert p256.pem -key p256.key
check "client certificate asked" ok localhost -cert p256.pem -key p256.key -verify 1
check "TLS 1.2 only"           fail localhost -cert p256.pem -key p256.key -no_tls1_3
check "P-256 key share only"   fail localhost -cert p256.pem -key p256.key -groups P-256
check "wrong host name"        fail 127.0.0.1 -cert named.pem -key named.key
check "unknown root"           fail localhost -cert self.pem -key p256.key
echo "$pass passed, $fail failed"
exit $fail
