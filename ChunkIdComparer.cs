using System;
using System.Collections.Generic;
using System.Linq;

namespace SteamFileDownloader;

class ChunkIdComparer : IEqualityComparer<byte[]>
{
    public bool Equals(byte[]? x, byte[]? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x == null || y == null) return false;
        return x.SequenceEqual(y);
    }

    public int GetHashCode(byte[] obj)
    {
        ArgumentNullException.ThrowIfNull(obj);

        // ChunkID is SHA-1, so we can just use the first 4 bytes
        return BitConverter.ToInt32(obj, 0);
    }
}
