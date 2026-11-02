#!/usr/bin/env python3
"""Builds known-answer encrypted pages from the published SQLCipher design using only OpenSSL and the Python standard library, so the
page codec is checked against primitives that share no code with it. Prints the hex constants used by EncryptionKnownAnswerTests.

Layout (SQLCipher design page): ciphertext | IV | HMAC(ciphertext || IV || page number as 4 little-endian bytes) | zero padding.
Page 1 starts with the 16-byte salt and encrypts only the bytes after it. Keys: PBKDF2(passphrase, salt, iterations, kdf digest) for AES-256,
then PBKDF2(that key, salt XOR 0x3a, 2 iterations, kdf digest) for the MAC key.
"""
import hashlib, hmac, struct, subprocess, sys

def aes_cbc(key, iv, data):
    return subprocess.run(["openssl", "enc", "-aes-256-cbc", "-nopad", "-K", key.hex(), "-iv", iv.hex()], input=data, capture_output=True, check=True).stdout

def page(label, pw, salt, iters, kdf, hmac_alg, size, reserve, number, iv):
    key = hashlib.pbkdf2_hmac(kdf, pw, salt, iters, 32)
    mkey = hashlib.pbkdf2_hmac(kdf, key, bytes(b ^ 0x3A for b in salt), 2, 32)
    plain = bytes((i * 7 + number) & 0xFF for i in range(size))
    start = 16 if number == 1 else 0
    end = size - reserve
    ct = aes_cbc(key, iv, plain[start:end])
    body = (salt if number == 1 else b"") + ct + iv
    mac = hmac.new(mkey, ct + iv + struct.pack("<I", number), hmac_alg).digest()
    stored = body + mac
    stored += bytes(size - len(stored))
    assert len(stored) == size
    print(f"// {label}")
    print(f"{label.replace(' ', '_')} {iters} {number} {iv.hex()} {stored.hex()}")

salt = bytes(range(0xA0, 0xB0))
iv = bytes(range(0x10, 0x20))
page("V4 page 2", b"test123", salt, 256000, "sha512", "sha512", 1024, 80, 2, iv)
page("V4 page 1", b"test123", salt, 256000, "sha512", "sha512", 1024, 80, 1, iv)
page("V3 page 2", b"test123", salt, 64000, "sha1", "sha1", 1024, 48, 2, iv)
