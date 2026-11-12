namespace Encspect.Formats.Vorbis.Decoding;

// The vectors a codebook's value lookup gives (Vorbis I specification, section 3.2.1) after libvorbis 1.3.7's _book_unquantize (lib/sharedbook.c, BSD-3-Clause,
// Copyright (c) 2002-2020 Xiph.Org Foundation; the notice is in THIRD-PARTY-NOTICES.md): each value is |multiplicand| * delta + minimum, plus the value before it where the
// book is a sequence, computed in double and rounded to float once (libvorbis's C expression promotes the same way; a build that rounds each step would differ in the last bit).
internal static class CodebookVectors
{
    // The vector of every entry that has a codeword, entry by entry (entry * dimensions + k), or null for a book without a lookup.
    public static float[]? Build(Codebook book)
    {
        if (book.LookupType == 0 || book.Dimensions < 1)
        {
            return null;
        }

        int dimensions = book.Dimensions;
        var values = new float[(long)book.EntryCount * dimensions];
        long quantValues = book.LookupType == 1 ? Codebook.Lookup1Values(book.EntryCount, dimensions) : 0;
        for (int entry = 0; entry < book.EntryCount; entry++)
        {
            if (book.Lengths[entry] == 0)
            {
                continue;
            }

            float last = 0f;
            long divisor = 1;
            for (int k = 0; k < dimensions; k++)
            {
                long index = book.LookupType == 1 ? entry / divisor % quantValues : ((long)entry * dimensions) + k;
                float value = (float)((Math.Abs((double)book.Multiplicands[index]) * book.Delta) + book.Minimum + last);
                if (book.IsSequence)
                {
                    last = value;
                }

                values[((long)entry * dimensions) + k] = value;
                if (book.LookupType == 1)
                {
                    divisor *= quantValues;
                }
            }
        }

        return values;
    }
}
