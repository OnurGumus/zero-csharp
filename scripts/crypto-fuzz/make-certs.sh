#!/usr/bin/env bash
# Makes the certificates samples/Certificates.cs checks, with OpenSSL 3.4 or
# newer, and prints them as C# string constants to paste into the sample.
# The keys are new each run, so the output differs every time.
#
#   root       RSA 2048, self-signed, SHA-256            "Zero Test Root R1"
#   inter      P-384, signed by root, path length 0      "Zero Test CA E1"
#   leaf       P-256, signed by inter with SHA-384       www.example.test
#   rootE      P-256 self-signed root, ECDSA SHA-256     "Zero Test Root E2"
#   leafRsa    RSA 3072 leaf signed by rootE             rsa.example.test
#   expired    like leaf, 2020 to 2021
#   forged     like leaf, issuer name of inter but signed by another key
#   notCa      a CA:FALSE certificate used as an issuer, and its child
#   tooDeep    a sub-CA under inter (path length 0), and its child
#   client     leaf with extended key usage clientAuth only
#   critical   leaf with an unknown extension marked critical
#   sha1       leaf signed by inter with ECDSA-SHA1
#   pss        leaf signed by root with RSA-PSS (not accepted in certificates here)
#
# Usage: scripts/crypto-fuzz/make-certs.sh > certs.txt
set -euo pipefail
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
cd "$work"

v() { printf '%s' "-not_before 20260101000000Z -not_after 20270101000000Z"; }

ext() { # name, then lines
    local name=$1; shift
    printf '%s\n' "[x]" "$@" > "$name.ext"
}

key_rsa() { openssl genpkey -algorithm RSA -pkeyopt "rsa_keygen_bits:$2" -out "$1.key" 2>/dev/null; }
key_ec() { openssl genpkey -algorithm EC -pkeyopt "ec_paramgen_curve:$2" -out "$1.key" 2>/dev/null; }

# self-signed: name, subject, ext file, digest
self() {
    openssl req -new -x509 -key "$1.key" -subj "$2" -extensions x -config "$3.ext" -"$4" \
        $(v) -set_serial "0x$(openssl rand -hex 8)" -out "$1.pem" 2>/dev/null
}

# issued: name, subject, issuer, ext file, digest, [dates]
issue() {
    openssl req -new -key "$1.key" -subj "$2" -out "$1.csr" 2>/dev/null
    openssl x509 -req -in "$1.csr" -CA "$3.pem" -CAkey "$3.key" -extfile "$4.ext" -extensions x \
        -"$5" ${6:-$(v)} -set_serial "0x$(openssl rand -hex 8)" -out "$1.pem" 2>/dev/null
}

ext ca "basicConstraints=critical,CA:TRUE" "keyUsage=critical,keyCertSign,cRLSign" "subjectKeyIdentifier=hash"
ext ca0 "basicConstraints=critical,CA:TRUE,pathlen:0" "keyUsage=critical,keyCertSign,cRLSign" \
    "extendedKeyUsage=serverAuth,clientAuth" "subjectKeyIdentifier=hash" "authorityKeyIdentifier=keyid"
ext leaf "basicConstraints=critical,CA:FALSE" "keyUsage=critical,digitalSignature" "extendedKeyUsage=serverAuth" \
    "subjectAltName=DNS:www.example.test,DNS:*.api.example.test,IP:192.0.2.7,IP:2001:db8::7" \
    "subjectKeyIdentifier=hash" "authorityKeyIdentifier=keyid"
ext rsaleaf "basicConstraints=critical,CA:FALSE" "keyUsage=critical,digitalSignature,keyEncipherment" \
    "extendedKeyUsage=serverAuth" "subjectAltName=DNS:rsa.example.test"
ext notca "basicConstraints=critical,CA:FALSE" "keyUsage=critical,digitalSignature,keyCertSign"
ext client "basicConstraints=critical,CA:FALSE" "extendedKeyUsage=clientAuth" "subjectAltName=DNS:client.example.test"
ext critical "basicConstraints=critical,CA:FALSE" "subjectAltName=DNS:critical.example.test" \
    "1.3.6.1.4.1.55555.1=critical,ASN1:NULL"
ext plain "basicConstraints=critical,CA:FALSE" "subjectAltName=DNS:plain.example.test"

key_rsa root 2048;  self root "/C=US/O=Zero Test/CN=Zero Test Root R1" ca sha256
key_ec inter secp384r1; issue inter "/C=US/O=Zero Test/CN=Zero Test CA E1" root ca0 sha256
key_ec leaf prime256v1; issue leaf "/C=US/O=Example, Inc./CN=www.example.test" inter leaf sha384
key_ec rootE prime256v1; self rootE "/O=Zero Test/CN=Zero Test Root E2" ca sha256
key_rsa leafRsa 3072; issue leafRsa "/CN=rsa.example.test" rootE rsaleaf sha256
key_ec expired prime256v1
issue expired "/CN=www.example.test" inter leaf sha384 "-not_before 20200101000000Z -not_after 20210101000000Z"
# forged: a CA with the same name as inter but its own key signs the leaf.
key_ec fake secp384r1; self fake "/C=US/O=Zero Test/CN=Zero Test CA E1" ca sha256
key_ec forged prime256v1; issue forged "/CN=www.example.test" fake leaf sha384
key_ec notCa prime256v1; issue notCa "/CN=Not A CA" inter notca sha384
key_ec notCaChild prime256v1; issue notCaChild "/CN=plain.example.test" notCa plain sha256
key_ec subCa prime256v1; issue subCa "/CN=Zero Test Sub CA" inter ca sha384
key_ec deepChild prime256v1; issue deepChild "/CN=plain.example.test" subCa plain sha256
key_ec client prime256v1; issue client "/CN=client.example.test" inter client sha384
key_ec critical prime256v1; issue critical "/CN=critical.example.test" inter critical sha384
key_ec sha1 prime256v1; issue sha1 "/CN=plain.example.test" inter plain sha1
key_ec pss prime256v1
openssl req -new -key pss.key -subj "/CN=plain.example.test" -out pss.csr 2>/dev/null
openssl x509 -req -in pss.csr -CA root.pem -CAkey root.key -extfile plain.ext -extensions x -sha256 \
    -sigopt rsa_padding_mode:pss -sigopt rsa_pss_saltlen:digest $(v) -set_serial 0x0102 -out pss.pem 2>/dev/null

for name in root inter leaf rootE leafRsa expired forged notCa notCaChild subCa deepChild client critical sha1 pss; do
    printf '    const string %s = """\n' "$(tr '[:lower:]' '[:upper:]' <<< "${name:0:1}")${name:1}Pem"
    sed 's/^/        /' "$name.pem"
    printf '        """;\n\n'
done
