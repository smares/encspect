"""An independent walk of Vorbis audio packets (Vorbis I specification, sections 3.2.1, 4.3, 7.2.3 and 8.6): the floors and the residues of each packet read with the setup
header's codebooks, to the figure the analyzer reports as "Coded Bandwidth" (the end of the highest residue partition that reads a codebook entry, in coefficients). It is
written from the specification, with libvorbis 1.3.7's reading of what the specification leaves open (a type 1 partition reads ceil(size / dimensions) entries, a type 0
partition floor(size / dimensions), a packet that ends reads as far as it goes), so that vorbis_opus_model.py can state what the analyzer must report without the analyzer's code.
Nothing here is reconstruction: no value is built from an entry.
"""

from vorbis_opus_model import BitReader, ilog


class Tree:
    """The Huffman code of a codebook: codewords assigned in entry order, each the leftmost free node of its length (section 3.2.1)."""

    def __init__(self, lengths):
        self.root = [None, None, None]  # child 0, child 1, entry
        self.single = sum(1 for l in lengths if l) == 1 and max(lengths) == 1
        for entry, length in enumerate(lengths):
            if length:
                path = self.free(self.root, 0, length, [])
                if path is None:
                    raise ValueError("overpopulated")
                node = self.root
                for bit in path:
                    if node[bit] is None:
                        node[bit] = [None, None, None]
                    node = node[bit]
                node[2] = entry

    def free(self, node, depth, length, path):
        if node[2] is not None:
            return None
        if depth == length:
            return path if node[0] is None and node[1] is None else None
        for bit in (0, 1):
            child = node[bit]
            found = self.free(child, depth + 1, length, path + [bit]) if child is not None else path + [bit] + [0] * (length - depth - 1)
            if found is not None:
                return found
        return None

    def decode(self, reader):
        """The entry of the next codeword, or None where the packet ends before one is whole."""
        if self.single:
            try:
                reader.read(1)
            except EOFError:
                return None
            return self.root[0][2]
        node = self.root
        start = reader.position
        while node[2] is None:
            try:
                bit = reader.read(1)
            except EOFError:
                reader.position = reader.length
                return None
            node = node[bit]
            if node is None:
                reader.position = reader.length
                return None
        return node[2]


class Setup:
    def __init__(self, packet, channels):
        r = BitReader(packet, 56)
        self.channels = channels
        self.books = []
        for _ in range(r.read(8) + 1):
            r.read(24)
            dimensions, entries = r.read(16), r.read(24)
            lengths = [0] * entries
            if r.read(1) == 0:
                sparse = r.read(1)
                for e in range(entries):
                    if sparse and not r.read(1):
                        continue
                    lengths[e] = r.read(5) + 1
            else:
                current, length = 0, r.read(5) + 1
                while current < entries:
                    number = r.read(ilog(entries - current))
                    for e in range(current, current + number):
                        lengths[e] = length
                    current += number
                    length += 1
            lookup = r.read(4)
            if lookup in (1, 2):
                r.read(32)
                r.read(32)
                bits = r.read(4) + 1
                r.read(1)
                if lookup == 1:
                    values = 0
                    while (values + 1) ** dimensions <= entries:
                        values += 1
                else:
                    values = entries * dimensions
                r.position += values * bits
            self.books.append((dimensions, entries, Tree(lengths), sum(1 for l in lengths if l)))
        for _ in range(r.read(6) + 1):
            r.read(16)
        self.floors = []
        for _ in range(r.read(6) + 1):
            kind = r.read(16)
            if kind == 0:
                r.read(8), r.read(16), r.read(16), r.read(6), r.read(8)
                self.floors.append((0, [r.read(8) for _ in range(r.read(4) + 1)], r.read))
            else:
                partitions = r.read(5)
                classes = [r.read(4) for _ in range(partitions)]
                class_dims, class_subs, class_master, class_books = [], [], [], []
                for _ in range(max(classes, default=-1) + 1):
                    class_dims.append(r.read(3) + 1)
                    subs = r.read(2)
                    class_subs.append(subs)
                    class_master.append(r.read(8) if subs else None)
                    class_books.append([r.read(8) - 1 for _ in range(1 << subs)])
                multiplier = r.read(2) + 1
                bits = r.read(4)
                for c in classes:
                    for _ in range(class_dims[c]):
                        r.read(bits)
                self.floors.append((1, classes, class_dims, class_subs, class_master, class_books, multiplier))
        self.residues = []
        for _ in range(r.read(6) + 1):
            kind = r.read(16)
            begin, end, size = r.read(24), r.read(24), r.read(24) + 1
            classifications = r.read(6) + 1
            classbook = r.read(8)
            cascades = []
            for _ in range(classifications):
                low = r.read(3)
                cascades.append(low | (r.read(5) << 3 if r.read(1) else 0))
            books = [[(r.read(8) if cascade >> bit & 1 else None) for bit in range(8)] for cascade in cascades]
            self.residues.append((kind, begin, end, size, classifications, classbook, books))
        self.mappings = []
        for _ in range(r.read(6) + 1):
            r.read(16)
            submaps = r.read(4) + 1 if r.read(1) else 1
            coupling = []
            if r.read(1):
                for _ in range(r.read(8) + 1):
                    coupling.append((r.read(ilog(channels - 1)), r.read(ilog(channels - 1))))
            r.read(2)
            mux = [r.read(4) for _ in range(channels)] if submaps > 1 else [0] * channels
            submap = []
            for _ in range(submaps):
                r.read(8)
                submap.append((r.read(8), r.read(8)))
            self.mappings.append((submaps, coupling, mux, submap))
        self.modes = []
        for _ in range(r.read(6) + 1):
            flag, _, _, mapping = r.read(1), r.read(16), r.read(16), r.read(8)
            self.modes.append((flag, mapping))
        self.mode_bits = ilog(len(self.modes) - 1)


def floor1_nonzero(setup, floor, reader):
    """Reads a floor 1 packet (section 7.2.3); True where the floor is used, False where it is unused or the packet ends."""
    _, classes, class_dims, class_subs, class_master, class_books, multiplier = floor
    try:
        if reader.read(1) == 0:
            return False
        amplitude_bits = ilog({1: 255, 2: 127, 3: 85, 4: 63}[multiplier])
        reader.read(amplitude_bits)
        reader.read(amplitude_bits)
    except EOFError:
        return False
    for cls in classes:
        subs = class_subs[cls]
        cval = 0
        if subs:
            cval = setup.books[class_master[cls]][2].decode(reader)
            if cval is None:
                return False
        for _ in range(class_dims[cls]):
            book = class_books[cls][cval & ((1 << subs) - 1)]
            cval >>= subs
            if book >= 0 and setup.books[book][2].decode(reader) is None:
                return False
    return True


def residue_end(setup, residue, reader, n, count, rows):
    """The end of the highest partition coded of one residue decoding over `count` vectors (type 2: one interleaved vector of `count` channels), in coefficients of
    one channel. `rows` is the number of vectors read (the channels that are not marked do not decode, or 1 for type 2)."""
    kind, begin, end, size, classifications, classbook, books = residue
    maximum = n // 2 * (count if kind == 2 else 1)
    end = min(end, maximum)
    if end - begin <= 0:
        return 0
    partitions = (end - begin) // size
    dims, entries, tree, _ = setup.books[classbook]
    coded = 0
    words = [[None] * partitions for _ in range(rows)]
    for stage in range(8):
        i = 0
        while i < partitions:
            if stage == 0:
                for j in range(rows):
                    temp = tree.decode(reader)
                    if temp is None or temp >= classifications ** dims:
                        return coded
                    digits = []
                    for _ in range(dims):
                        digits.append(temp % classifications)
                        temp //= classifications
                    for k in range(dims):
                        if i + k < partitions:
                            words[j][i + k] = digits[dims - 1 - k]
            for k in range(dims):
                if i >= partitions:
                    break
                for j in range(rows):
                    cls = words[j][i]
                    book = books[cls][stage]
                    if book is None:
                        continue
                    bdims, bentries, btree, used = setup.books[book]
                    if used == 0:
                        continue
                    if bdims == 0:
                        if kind != 0:
                            while btree.decode(reader) is not None:
                                pass
                            return coded
                        continue
                    offset = begin + i * size
                    if kind == 0:
                        wanted = size // bdims
                    elif kind == 1:
                        wanted = -(-size // bdims)
                    else:
                        values = ((offset + size) // count - offset // count) * count
                        wanted = -(-values // bdims)
                    read = 0
                    while read < wanted:
                        if btree.decode(reader) is None:
                            break
                        read += 1
                    if read and (read == wanted or kind != 0):
                        coded = max(coded, (offset + size) // count if kind == 2 else offset + size)
                    if read < wanted:
                        return coded
                i += 1
    return coded


def coded_end(setup, packet):
    """(the block size, the coded end of one audio packet in coefficients), or None where the packet codes no block (not audio, empty, a mode the setup lacks)."""
    if not packet or packet[0] & 1:
        return None
    reader = BitReader(packet, 1)
    try:
        mode = reader.read(setup.mode_bits)
        if mode >= len(setup.modes):
            return None
        long_block = setup.modes[mode][0]
        if long_block:
            reader.read(2)
    except EOFError:
        return None
    n = setup.blocksizes[1 if long_block else 0]
    return n, walk_packet(setup, reader, mode, n)


def walk_packet(setup, reader, mode, n):
    submaps, coupling, mux, submap = setup.mappings[setup.modes[mode][1]]
    nonzero = []
    for ch in range(setup.channels):
        floor = setup.floors[submap[mux[ch]][0]]
        nonzero.append(floor1_nonzero(setup, floor, reader) if floor[0] == 1 else False)
    for magnitude, angle in coupling:
        if nonzero[magnitude] or nonzero[angle]:
            nonzero[magnitude] = nonzero[angle] = True
    coded = 0
    for s in range(submaps):
        channels = [ch for ch in range(setup.channels) if mux[ch] == s]
        residue = setup.residues[submap[s][1]]
        if residue[0] == 2:
            if any(nonzero[ch] for ch in channels):
                coded = max(coded, residue_end(setup, residue, reader, n, len(channels), 1))
        else:
            used = [ch for ch in channels if nonzero[ch]]
            if used:
                coded = max(coded, residue_end(setup, residue, reader, n, 1, len(used)))
    return coded


def hertz(ident, setup_packet, packets):
    """The coded bandwidth of each audio packet in whole hertz, the upper edge of the last coded coefficient rounded half up (None for a packet that codes no block):
    ident and setup_packet are the identification and setup headers' bytes."""
    rate = int.from_bytes(ident[12:16], "little")
    setup = Setup(setup_packet, ident[11])
    setup.blocksizes = (1 << (ident[28] & 15), 1 << (ident[28] >> 4))
    out = []
    for packet in packets:
        found = coded_end(setup, packet)
        out.append(None if found is None else (2 * found[1] * rate + found[0]) // (2 * found[0]))
    return out
