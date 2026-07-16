namespace Packet.SoundModem.Fec.Ldpc;

/// <summary>
/// Log-domain sum-product LDPC decoder — a faithful port of codec2's
/// <c>init_c_v_nodes</c> + <c>SumProduct</c> (<c>mpdecode_core.c</c>) for the
/// <c>H1 = 1, shift = 0</c> RA / dual-diagonal branch that every FreeDV datac code takes.
/// The Tanner graph (edge indices + reciprocal sockets) is built once per code and cached;
/// <see cref="Decode"/> re-initialises the messages from each received LLR vector, exactly as
/// codec2 rebuilds them per <c>run_ldpc_decoder</c> call. Edge ordering and the socket search
/// are reproduced verbatim so the float accumulation — and thus iteration counts and
/// early-termination — match. Not thread-safe (the graph carries per-decode message state).
/// </summary>
public sealed class LdpcDecoder
{
    // c-node edge → a variable node: (which v-node, the reciprocal socket in that v-node,
    // the check→variable message).
    private struct CSub
    {
        public int VIndex;
        public int VSocket;
        public float Message;
    }

    // v-node edge → a check node: (which c-node, the reciprocal socket, the variable→check
    // message in the φ0 domain, and its sign carried separately — Gallager φ representation).
    private struct VSub
    {
        public int CIndex;
        public int CSocket;
        public float Message;
        public bool Sign;
    }

    private sealed class CNode
    {
        public CSub[] Subs = default!;
    }

    private sealed class VNode
    {
        public float InitialValue;
        public VSub[] Subs = default!;
    }

    private readonly LdpcCode _c;
    private readonly CNode[] _cn;   // length NumberParityBits (M)
    private readonly VNode[] _vn;   // length CodeLength (N)

    /// <summary>Builds and caches the decoder for <paramref name="code"/>.</summary>
    public LdpcDecoder(LdpcCode code)
    {
        _c = code;
        int m = code.NumberParityBits, n = code.CodeLength, k = code.NumberRowsHcols;
        // shift = (M+K)-N; H1 = (K != N). Every datac code is RA: K = M = N/2 ⇒ shift 0, H1 1.
        if (k == n || (m + k) - n != 0)
        {
            throw new ArgumentException(
                $"{code.Name} is not an RA (H1=1, shift=0) code; only those are supported", nameof(code));
        }

        _cn = new CNode[m];
        _vn = new VNode[n];
        BuildGraph(m, n, k);
    }

    private void BuildGraph(int m, int n, int k)
    {
        ushort[] rows = _c.HRows, cols = _c.HCols;
        int maxRow = _c.MaxRowWeight, maxCol = _c.MaxColWeight;

        // c-node degrees (shift==0, H1): mpdecode_core.c:151-166.
        for (int i = 0; i < m; i++)
        {
            int count = 0;
            for (int j = 0; j < maxRow; j++)
            {
                if (rows[i + j * m] > 0)
                {
                    count++;
                }
            }

            int degree = i == 0 ? count + 1 : count + 2;
            _cn[i] = new CNode { Subs = new CSub[degree] };
        }

        // c-node sub indices (shift==0, H1): mpdecode_core.c:197-210.
        for (int i = 0; i < m; i++)
        {
            int degree = _cn[i].Subs.Length;
            for (int j = 0; j < degree - 2; j++)
            {
                _cn[i].Subs[j].VIndex = rows[i + j * m] - 1;
            }

            int last = degree - 2;
            _cn[i].Subs[last].VIndex = i == 0 ? rows[i + last * m] - 1 : (n - m) + i - 1;
            _cn[i].Subs[degree - 1].VIndex = (n - m) + i;
        }

        // v-node degrees: mpdecode_core.c:261-288 (shift==0).
        for (int i = 0; i < n; i++)
        {
            int degree;
            if (i < n - m)
            {
                int count = 0;
                for (int j = 0; j < maxCol; j++)
                {
                    if (cols[i + j * k] > 0)
                    {
                        count++;
                    }
                }

                degree = count;
            }
            else
            {
                degree = i != n - 1 ? 2 : 1;   // H1 dual-diagonal
            }

            _vn[i] = new VNode { Subs = new VSub[degree] };
        }

        // v-node sub indices + reciprocal-socket search: mpdecode_core.c:296-326.
        for (int i = 0; i < n; i++)
        {
            int degree = _vn[i].Subs.Length, count = 0;
            for (int j = 0; j < degree; j++)
            {
                int idx;
                if (i >= n - m)          // H1, shift==0: parity column
                {
                    idx = i - (n - m) + count;
                    count += 1;
                }
                else
                {
                    idx = cols[i + j * k] - 1;
                }

                _vn[i].Subs[j].CIndex = idx;
                CSub[] csubs = _cn[idx].Subs;
                for (int ci = 0; ci < csubs.Length; ci++)
                {
                    if (csubs[ci].VIndex == i)
                    {
                        _vn[i].Subs[j].CSocket = ci;
                        break;
                    }
                }
            }
        }

        // finish c-node reciprocal sockets: mpdecode_core.c:338-349.
        for (int i = 0; i < m; i++)
        {
            CSub[] csubs = _cn[i].Subs;
            for (int j = 0; j < csubs.Length; j++)
            {
                int vidx = csubs[j].VIndex;
                VSub[] vsubs = _vn[vidx].Subs;
                for (int vi = 0; vi < vsubs.Length; vi++)
                {
                    if (vsubs[vi].CIndex == i)
                    {
                        _cn[i].Subs[j].VSocket = vi;
                        break;
                    }
                }
            }
        }
    }

    /// <summary>Decodes <paramref name="input"/> (CodeLength LLRs, positive ⇒ bit 0) to
    /// <paramref name="decoded"/> (CodeLength hard bits). Returns the iteration count.</summary>
    public int Decode(ReadOnlySpan<float> input, Span<byte> decoded, out int parityCheckCount)
    {
        int m = _c.NumberParityBits, n = _c.CodeLength;
        parityCheckCount = 0;

        // Re-initialise every edge from the received LLRs (dec_type==0): mpdecode_core.c:304-333.
        for (int i = 0; i < n; i++)
        {
            _vn[i].InitialValue = input[i];
            float phi = Phi0.Compute(MathF.Abs(input[i]));
            bool sign = input[i] < 0;
            VSub[] subs = _vn[i].Subs;
            for (int j = 0; j < subs.Length; j++)
            {
                subs[j].Message = phi;
                subs[j].Sign = sign;
            }
        }

        int result = _c.MaxIter;
        for (int iter = 0; iter < _c.MaxIter; iter++)
        {
            decoded[..n].Clear();   // DecodedBits[i] = 0 each pass
            int ssum = 0;

            // update r: check → variable (mpdecode_core.c:375-402).
            for (int j = 0; j < m; j++)
            {
                CSub[] cs = _cn[j].Subs;
                VSub first = _vn[cs[0].VIndex].Subs[cs[0].VSocket];
                bool sign = first.Sign;
                float phiSum = first.Message;
                for (int i = 1; i < cs.Length; i++)
                {
                    VSub vp = _vn[cs[i].VIndex].Subs[cs[i].VSocket];
                    phiSum += vp.Message;
                    sign ^= vp.Sign;
                }

                if (!sign)
                {
                    ssum++;
                }

                for (int i = 0; i < cs.Length; i++)
                {
                    VSub vp = _vn[cs[i].VIndex].Subs[cs[i].VSocket];
                    float msg = Phi0.Compute(phiSum - vp.Message);
                    cs[i].Message = (sign ^ vp.Sign) ? -msg : msg;
                }
            }

            // update q: variable → check (mpdecode_core.c:405-428).
            for (int i = 0; i < n; i++)
            {
                VSub[] vs = _vn[i].Subs;
                float qi = _vn[i].InitialValue;
                for (int j = 0; j < vs.Length; j++)
                {
                    qi += _cn[vs[j].CIndex].Subs[vs[j].CSocket].Message;
                }

                if (qi < 0)
                {
                    decoded[i] = 1;
                }

                for (int j = 0; j < vs.Length; j++)
                {
                    float temp = qi - _cn[vs[j].CIndex].Subs[vs[j].CSocket].Message;
                    vs[j].Message = Phi0.Compute(MathF.Abs(temp));
                    vs[j].Sign = !(temp > 0);   // C: sign = (temp_sum > 0) ? 0 : 1
                }
            }

            // Halt if the first N-M (data) bits are all zero. codec2 compares against
            // data_int[], which run_ldpc_decoder CALLOCs to zeros (mpdecode_core.c:432-439,503)
            // — replicated verbatim so iteration counts match for interop parity.
            bool allZeroData = true;
            for (int i = 0; i < n - m; i++)
            {
                if (decoded[i] != 0)
                {
                    allZeroData = false;
                    break;
                }
            }

            if (allZeroData)
            {
                result = iter + 1;
                break;
            }

            parityCheckCount = ssum;   // set only past the all-zero-data halt (mpdecode_core.c:442)
            if (ssum == m)
            {
                result = iter + 1;
                break;
            }
        }

        return result;
    }
}
