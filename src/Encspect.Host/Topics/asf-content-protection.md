---
id: asf-content-protection
title: Content protection in ASF
related: asf-container, asf-packets
references: Advanced Systems Format (ASF) Specification, revision 01.20.03, sections 3.3, 3.14, 3.15, 3.16, 4.13 and 10.7
---

**Where it lives.** An ASF file marks protection in its header. Three objects describe it, each for a different generation of Microsoft's rights management, and a fourth signs the header.

**Content Encryption Object (3.14).** Protection by Microsoft Digital Rights Manager version 1. It holds secret data, the protection type ("DRM"), a key ID and the URL where a license can be acquired.

**Extended Content Encryption Object (3.15).** Protection by the Windows Media Rights Manager 7 SDK. It holds a block of data that the rights manager reads; the specification gives it no structure.

**Advanced Content Encryption Object (4.13).** Protection by Windows Media DRM for network devices. It holds content encryption records. Each names a system by its ID (section 10.7 lists one, for Windows Media DRM for network devices), its version, the objects it covers and its data. An object is an ASF stream number, where 0 means the whole file.

**Digital Signature Object (3.16).** A signature of the part of the header between the File Properties Object and itself. It protects nothing, and a file with only a signature is not protected.

**Stream flag.** Each Stream Properties Object has an Encrypted Content Flag (3.3) that says whether that stream's data is encrypted.

**What you see.** The object nodes with their fields, and the streams marked protected with the scheme. The analyzer does not decrypt or fetch licenses; the media objects of an encrypted stream are not read, and the stream is described from its WAVEFORMATEX alone.
