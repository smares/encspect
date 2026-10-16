---
id: matroska-content-encryption
title: ContentEncryption in Matroska
related: matroska-tracks, common-encryption
references: RFC 9559 sections 5.1.4.1.31.3, 5.1.4.1.31.4 and 5.1.4.1.31.8 to 5.1.4.1.31.12 (Tables 21 to 27) and section 14 (Encryption)
---

**What it is.** A track's ContentEncodings can encrypt its data. A ContentEncoding of type 1 holds a ContentEncryption element; under any other type the element must be ignored. Because the encryption is applied inside each block, a file can be cut, copied or appended without decrypting it.

**The algorithm.** ContentEncAlgo names it: 0 not encrypted, 1 DES, 2 3DES, 3 Twofish, 4 Blowfish, 5 AES. Algorithm 0, also the default value, states that the data are not encrypted. For AES, ContentEncAESSettings holds AESSettingsCipherMode, 1 for AES-CTR and 2 for AES-CBC; the settings must not be present for another algorithm. ContentEncKeyID names a key and says nothing about a DRM system. Key exchange is outside the specification.

**The scope.** ContentEncodingScope is a bit field: 1 is the frame contents, 2 the track's CodecPrivate, 4 the next ContentEncoding, meaning the settings inside it, which players do not support. Encodings are undone from the highest ContentEncodingOrder down, so a compression of a higher order than an encryption has to be undone first. Encspect treats an encoding that another one hides with bit 4 as unreadable: its parts count as encrypted by the hiding encryption, whatever the hidden encoding states. When a compression hides an encryption, or an encryption is hidden further up the chain, the hidden encoding counts as encrypted by an unknown algorithm. An encryption whose scope covers neither the frames nor the CodecPrivate encrypts nothing and is not reported as protection.

**What you see.** The ContentEncoding elements, with the scope, the type, the algorithm and the cipher mode. The analyzer reads none of the keys and decrypts nothing: a track whose frames are encrypted is reported as a protected, encrypted stream with its scheme, and its frames are not read, since ciphertext is no valid coding. When only the CodecPrivate is encrypted, the frames are in the clear but the codec's setup is not available. Findings say where an element is missing, ignored or not allowed.
