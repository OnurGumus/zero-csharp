---
title: Use cryptography
index: 31
---

# Use cryptography

`lib/` has the cryptography a TLS 1.3 connection is built from, with .NET's names from
`System.Security.Cryptography`, so code written against them compiles here:

- **Hashes:** `SHA256`, `SHA384` and `SHA512`, `HMACSHA256` and its siblings, `HKDF`, and
  `IncrementalHash` for data that comes in pieces.
- **Encryption:** `ChaCha20Poly1305` and `AesGcm`, which encrypt and authenticate in one step.
- **Key agreement:** `X25519`, our own class (.NET has no X25519).
- **Random bytes:** `RandomNumberGenerator`, from the operating system.
- **A stream:** `EncryptedStream`, also ours, which encrypts what goes through another stream in TLS
  1.3's record format.

It's plain C#, the same code on every target, and it runs in the same time whatever the keys and
data are: no branches or table lookups on secrets. `samples/Crypto.cs` checks it against the
standards' own test vectors (FIPS 180-4, RFC 4231, 5869, 7748, 8439, the GCM specification's, and
RFC 8448's TLS 1.3 handshake, whose client Finished record it decrypts).
`samples/CryptoErrors.cs` checks that changed data is refused, and `scripts/crypto-fuzz` compares
it with .NET's own on random input.

There is no TLS handshake yet, so no HTTPS. These are the pieces it will be built from.

## Hash and authenticate

<!-- sample: samples/Crypto.cs hash -->
```csharp
byte[] digest = SHA256.HashData("abc"u8);
byte[] mac = HMACSHA256.HashData("key"u8, "message"u8);
```

Each also has a `HashData(source, destination)` that writes into a span you give, with no
allocation. For data that comes in pieces, `IncrementalHash.CreateHash` or `CreateHMAC` takes
`AppendData` calls. `GetCurrentHash` gives the hash so far and lets appending go on, which is what a
TLS handshake's running transcript needs.

Compare MACs and tags with `CryptographicOperations.FixedTimeEquals`, never with a loop that stops at
the first difference: how long that takes tells an attacker how much of a forged tag was right.

`HKDF.DeriveKey` turns a secret into as many bytes of keys as you need, bound to a label (`info`),
so one secret can give several independent keys.

## Encrypt and authenticate

`ChaCha20Poly1305` takes a 32-byte key. Each message needs a 12-byte nonce that is never used twice
with that key. Count messages, or take 12 random bytes for each:

<!-- sample: samples/Crypto.cs seal -->
```csharp
// Encrypt: ciphertext as long as the plaintext, and a 16-byte tag
// over it and the associated data (sent as is, but protected).
byte[] ciphertext = new byte[plaintext.Length];
byte[] tag = new byte[16];
using var aead = new ChaCha20Poly1305(key);
aead.Encrypt(nonce, plaintext, ciphertext, tag, aad);
```

The other end opens it with the same key, nonce and associated data:

<!-- sample: samples/Crypto.cs open -->
```csharp
byte[] back = new byte[plaintext.Length];
aead.Decrypt(nonce, ciphertext, tag, back, aad);
```

If anything was changed, `Decrypt` throws `AuthenticationTagMismatchException` and writes zeros, not
the forged plaintext. The throw ends the program unless a [`Catch.Run`](recover-from-errors.html) is
waiting for it:

<!-- sample: samples/CryptoErrors.cs tag-mismatch -->
```csharp
// Decrypt checks the tag before anything else: a changed byte
// anywhere throws, and the destination gets zeros, not plaintext.
ciphertext[40] ^= 1;
Exception error = Catch.Run(
    () => aead.Decrypt(nonce, ciphertext, tag, back, aad));
```

A repeated nonce under one key gives away the XOR of the two plaintexts and lets anyone forge tags,
so that is the one rule never to break.

`AesGcm` works the same way, with a 16-, 24- or 32-byte key and a tag size (12 to 16 bytes) given to
its constructor, as in .NET. It's the cipher every TLS server offers. AES is normally a table lookup
per byte, and which entries a program reads shows in the processor's cache, where other code on the
machine can time it. This one has no tables: it works on the bits of four blocks at once
("bitsliced"), with the S-box as a circuit of ANDs and XORs. That costs some speed (about 85 MB/s on
one Apple M2 Max core, against ChaCha20-Poly1305's 106 MB/s), far more than a connection needs. There are
no AES instructions here: zero mode has no run-time CPU detection to choose them with.

## Random bytes

<!-- sample: samples/CryptoRandom.cs random-bytes -->
```csharp
Span<byte> key = stackalloc byte[32];
RandomNumberGenerator.Fill(key);
```

These come from the operating system, unlike `Random`'s, which anyone can predict from a few of
them. `GetInt32(from, to)` picks a number in a range with every value equally likely.

| Target | Where the bytes come from |
|---|---|
| Linux, Android | `getentropy` (the `getrandom` system call) |
| macOS, iOS | `getentropy` |
| Windows | `BCryptGenRandom` |
| WebAssembly | WASI's `random_get` (the browser page uses `crypto.getRandomValues`) |
| UEFI | the firmware's `EFI_RNG_PROTOCOL`. QEMU's firmware has one with a `virtio-rng` device or a CPU with RDRAND |
| DOS, microcontrollers, C64 | nothing: `Fill` throws `CryptographicException` |

The last row isn't an oversight: a PC under DOS has nothing a key could safely come from, so it gets
an error, not bytes that only look random. The hashes, `ChaCha20Poly1305` and `X25519` with keys you
give still work there.

## Agree on a key, then encrypt a connection

`X25519` lets two programs agree on a secret over an open connection: each sends a public key, and
both compute the same 32 bytes, which someone who saw only the public keys can't. Combined with
`EncryptedStream.FromSharedSecret` that is an encrypted connection:

<!-- sample: samples/Network.cs encrypted-handshake -->
```csharp
// Both ends send a fresh X25519 public key and agree on a secret; the
// stream then makes keys from it and random bytes each end adds.
// Nothing here proves who is at the other end: that's what TLS's
// certificates add.
static EncryptedStream Encrypt(NetworkStream stream, bool isInitiator)
{
    Span<byte> mine = stackalloc byte[32], myPublic = stackalloc byte[32];
    Span<byte> theirPublic = stackalloc byte[32], shared = stackalloc byte[32];
    X25519.GeneratePrivateKey(mine);
    X25519.GetPublicKey(mine, myPublic);
    stream.Write(myPublic);
    stream.ReadExactly(theirPublic);
    if (!X25519.TryGetSharedSecret(mine, theirPublic, shared))
        throw new IOException("the other end sent a bad key");
    EncryptedStream secure =
        EncryptedStream.FromSharedSecret(stream, shared, isInitiator);
    CryptographicOperations.ZeroMemory(mine);
    CryptographicOperations.ZeroMemory(shared);
    return secure;
}
```

`EncryptedStream` is a `Stream`: `Write` sends records of up to 16 KB, each sealed with
ChaCha20-Poly1305 under its own nonce, and `Read` opens them. A record that was changed, dropped,
repeated or reordered makes `Read` throw `IOException`:

<!-- sample: samples/CryptoErrors.cs record-refused -->
```csharp
// A changed byte anywhere in a record: Read throws IOException.
byte[] changed = wireBytes.AsSpan().ToArray();
changed[30] ^= 0x80;
Exception error = ReadAll(changed, keyB, ivB, keyA, ivA);
```

`Dispose` (or `Shutdown`) sends a closing record, and `Read` returns 0 only after one comes. A
connection that ends without it may have been cut short by someone in the middle, so `Read` throws
then too.

`FromSharedSecret` has each end send 32 random bytes first and mixes both into the keys. So every
connection gets new keys, even when the secret is one both programs keep for a long time. The
constructor that takes keys and IVs directly doesn't, so give it keys that are new for each
connection, as a TLS handshake's are.

What this doesn't do yet is tell you who is at the other end. Anyone can send a public key, so
someone in the middle could run one exchange with each side. TLS solves that with certificates,
which are the next step.
