using WebAirIG.Common.Transport;

namespace WebAirIG.Common.Video;

/// <summary>
/// Incrementally splits an Annex-B H.264/HEVC byte stream into access units (one coded picture each).
/// A new access unit starts at an AUD, at a parameter-set/SEI NAL that follows a VCL NAL, or at a VCL NAL whose
/// first-slice flag is set after the current AU already has a VCL NAL. Works with or without AUDs.
/// The last AU is emitted when the next one begins (one-frame latency) or on <see cref="Flush"/>.
/// </summary>
public sealed class AnnexBAccessUnitSplitter
{
    public delegate void AccessUnitHandler(byte[] accessUnit, bool isKeyframe);

    private readonly VideoCodec _codec;
    private byte[] _buf = new byte[1 << 20];
    private int _len;
    private int _scan;
    private int _auStart = -1;
    private bool _auHasVcl;
    private bool _auIsKey;
    private static readonly byte[] StartCode = [0, 0, 1];

    public AnnexBAccessUnitSplitter(VideoCodec codec) => _codec = codec;

    public void Push(ReadOnlySpan<byte> data, AccessUnitHandler onAccessUnit)
    {
        EnsureCapacity(_len + data.Length);
        data.CopyTo(_buf.AsSpan(_len));
        _len += data.Length;

        while (true)
        {
            int rel = _scan < _len ? _buf.AsSpan(_scan, _len - _scan).IndexOf(StartCode) : -1;
            if (rel < 0)
            {
                _scan = Math.Max(_scan, _len - 2);
                break;
            }
            int sc = _scan + rel;
            int hdr = sc + 3;
            if (hdr + 3 > _len)
            {
                _scan = sc; // need more bytes to classify this NAL
                break;
            }

            int boundary = sc > 0 && _buf[sc - 1] == 0 ? sc - 1 : sc;
            Classify(hdr, out bool isAud, out bool isPreVcl, out bool isVcl, out bool firstSlice, out bool isKey);

            bool newAu = _auStart < 0 || isAud || (isPreVcl && _auHasVcl) || (isVcl && firstSlice && _auHasVcl);
            if (newAu)
            {
                if (_auStart >= 0 && _auHasVcl)
                {
                    var au = _buf.AsSpan(_auStart, boundary - _auStart).ToArray();
                    onAccessUnit(au, _auIsKey);
                }
                // Discard everything before the new AU's start.
                Compact(boundary);
                hdr -= boundary;
                _auStart = 0;
                _auHasVcl = false;
                _auIsKey = false;
            }
            if (isVcl)
            {
                _auHasVcl = true;
                _auIsKey |= isKey;
            }
            _scan = hdr;
        }
    }

    /// <summary>Emits the trailing access unit (call at end of stream).</summary>
    public void Flush(AccessUnitHandler onAccessUnit)
    {
        if (_auStart >= 0 && _auHasVcl && _len > _auStart)
            onAccessUnit(_buf.AsSpan(_auStart, _len - _auStart).ToArray(), _auIsKey);
        _len = 0;
        _scan = 0;
        _auStart = -1;
        _auHasVcl = false;
        _auIsKey = false;
    }

    private void Classify(int hdr, out bool isAud, out bool isPreVcl, out bool isVcl, out bool firstSlice, out bool isKey)
    {
        if (_codec == VideoCodec.H264)
        {
            int t = _buf[hdr] & 0x1F;
            isAud = t == 9;
            isPreVcl = t is 6 or 7 or 8 or 14 or 15;
            isVcl = t is >= 1 and <= 5;
            firstSlice = isVcl && (_buf[hdr + 1] & 0x80) != 0; // first_mb_in_slice == 0
            isKey = t == 5;
        }
        else
        {
            int t = (_buf[hdr] >> 1) & 0x3F;
            isAud = t == 35;
            isPreVcl = t is 32 or 33 or 34 or 39;
            isVcl = t <= 31;
            firstSlice = isVcl && (_buf[hdr + 2] & 0x80) != 0; // first_slice_segment_in_pic_flag
            isKey = t is >= 16 and <= 21;
        }
    }

    private void Compact(int count)
    {
        if (count <= 0) return;
        Buffer.BlockCopy(_buf, count, _buf, 0, _len - count);
        _len -= count;
        _scan = Math.Max(0, _scan - count);
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _buf.Length) return;
        int size = _buf.Length;
        while (size < needed) size *= 2;
        Array.Resize(ref _buf, size);
    }
}
