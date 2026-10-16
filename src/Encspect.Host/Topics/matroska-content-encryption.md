---
id: matroska-content-encryption
title: ContentEncryption in Matroska
related: matroska-tracks, common-encryption
references: RFC 9559 sections 5.1.4.1.31.2 to 5.1.4.1.31.4 and 5.1.4.1.31.8 to 5.1.4.1.31.12 (Tables 21 to 27) and section 14 (Encryption)
---

**What it is.** A track's ContentEncodings can encrypt its data. A ContentEncoding of type 1 holds a ContentEncryption element; under any other type the element must be ignored.

**The algorithm.** ContentEncAlgo names it: 0 not encrypted, 1 DES, 2 3DES, 3 Twofish, 4 Blowfish, 5 AES. Algorithm 0, also the default, states that the data are not encrypted. For AES, ContentEncAESSettings holds AESSettingsCipherMode, 1 for AES-CTR and 2 for AES-CBC; it must not be present for another algorithm. ContentEncKeyID names a key.

**The scope.** ContentEncodingScope is a bit field: 1 is the frame contents, 2 the track's CodecPrivate, 4 the next ContentEncoding, meaning the settings inside it, which players do not support. Other bits are ignored; scope 0 covers nothing. Encodings are undone from the highest ContentEncodingOrder down. Encspect reads an encoding that bit 4 hides, and each one after it in the run, as unreadable whatever it states. If the outermost encoding of the run is an encryption, the hidden parts count as encrypted by it, and the scheme names it: its ciphertext hides the settings needed to read the frames. Otherwise they are encrypted by an unknown algorithm when the hidden encoding is of type 1 or an encryption is hidden further along the run, and not read when it is a compression. Orders must be unique; encodings that share one cannot be ordered, so none is trusted, nothing is read, and the track counts as encrypted if any is of type 1.

**What you see.** The ContentEncoding elements. Nothing is decrypted: a track whose frames are encrypted is reported as a protected, encrypted stream with its scheme, and its frames are not read, since ciphertext is no valid coding. When only the CodecPrivate is encrypted, the frames are in the clear but the codec's setup is not available.
