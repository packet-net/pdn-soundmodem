# MIL-STD-188-110D Appendix D — text-layer golden extracts

Source: MIL-STD-188-110D (29 Dec 2017), everyspec.com PDF (270 PDF pages).
Document page = PDF page − 5 throughout Appendix D.
These sections HAVE a PDF text layer; extracted with `pdftotext -layout` and reproduced
verbatim except where noted. The "Downloaded from https://www.everyspec.com" watermark
and running headers/footers are omitted.

## D.5.1.3 Data scrambling (document page 160, PDF page 165)

> Data symbols for Waveforms 1 through 7 and 13 (using BPSK, QPSK, or 8PSK modulation)
> shall be scrambled by modulo 8 addition with a scrambling sequence.
> The data symbols for Waveforms 8 through 12(16QAM, 32QAM, 64QAM and 256QAM
> modulation) shall be scrambled by using an exclusive or (XOR) operation. Sequentially, the
> data bits forming each symbol (4 for 16QAM, 5 for 32QAM, 6 for 64QAM and 8 for 256
> QAM) shall be XOR'd with an equal number of bits from the scrambling sequence.
> For Waveforms 1 through 13, the scrambling sequence generator polynomial shall be x9+x4+1
> and the generator shall be initialized to 1 at the start of each data frame. A block diagram of the
> scrambling sequence generator is shown in Figure D-6. In this illustration, three output bits are
> shown; this is the case for all PSK waveforms. For 2N QAM waveforms, the rightmost N bits
> are used.

Transcriber note: in the text layer the polynomial renders as `x +x +1  9   4` (superscripts
detached); in the rendered page it is x^9 + x^4 + 1. Likewise `2N QAM` is 2^N QAM.

> For PSK symbols (BPSK, QPSK, and 8PSK), the scrambling shall be carried out taking the
> modulo 8 sum of the numerical value of the binary triplet consisting of the last (rightmost)
> three bits in the shift register, and the symbol number (transcoded value). For example, if the
> last three bits in the scrambling sequence shift register were 010 which has a numerical value
> equal 2, and the symbol number before scrambling was 6, symbol 0 would be transmitted since
> (6+2) Modulo 8 = 0.
> For 16QAM symbols, scrambling shall be carried out by XORing the 4 bit number consisting
> of the last (rightmost) four bits in the shift register with the symbol number. For example, if the
> last 4 bits in the scrambling sequence shift register were 0101 and the 16QAM symbol number

(continues on document page 161, PDF page 166)

> before scrambling was 3 (i.e. 0011), symbol 6 (0110) would be transmitted. For 32QAM
> symbols, scrambling shall be carried out by XORing the 5 bit number formed by the last
> (rightmost) five bits in the shift register with the symbol number. For 64QAM symbols,
> scrambling shall be carried out by XORing the 6 bit number formed by the last (rightmost) six
> bits in the shift register with the symbol number. For 256QAM symbols, scrambling shall be
> carried out by XORing the 8 bit number formed by the last (rightmost) eight bits in the shift
> register with the symbol number.
> After each data symbol is scrambled, the generator shall be iterated (shifted) the required
> number of times to produce all new bits for use in scrambling the next symbol (i.e., 3 iterations
> for 8PSK, 4 iterations for 16QAM, 5 iterations for 32QAM and 6 iterations for 64QAM, and 8
> iterations for 256QAM). Since the generator is iterated after the bits are used, the first data
> symbol of every data frame shall, therefore, be scrambled by the appropriate number of bits
> from the initialization value of 00000001.
>
> The length of the scrambling sequence is 511 bits. For a 256 symbol data block with 6 bits per
> symbol, this means that the scrambling sequence will be repeated just slightly more than 3
> times, although in terms of symbols, there will be no repetition.

Transcriber note: the iteration list in the source omits BPSK/QPSK (it starts at "3 iterations
for 8PSK") — reproduced as printed.

## D.5.1.4 Waveform ID 0 Walsh Orthogonal Modulation Data scrambling (document pages 161-162, PDF pages 166-167)

> For the case of Waveform ID 0, an 8-PSK data scrambling sequence is utilized. This sequence
> is generated in a fashion similar to that described above but is based on a longer shift register of
> 159 bits with a single tap after bit 31. This is an implementation of a Trinomial (159, 31). The
> shift register is initialized to the following state:

```c
int bitshift[159] =
{0, 0, 0, 1, 0, 0, 1, 1, 0, 1, 1, 0, 0, 1, 0, 1,
 1, 1, 1, 1, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0,
 1, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 0, 1, 1, 1, 0,
 1, 1, 1, 0, 0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0,
 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 1, 0, 1,
 0, 1, 1, 1, 1, 0, 1, 0, 1, 0, 0, 0, 1, 1, 1, 1,
 1, 1, 0, 0, 1, 1, 0, 1, 0, 1, 1, 1, 1, 1, 0, 1,
 1, 1, 1, 0, 0, 0, 1, 1, 0, 0, 0, 1, 1, 0, 1, 0,
 1, 1, 1, 0, 0, 1, 1, 1, 0, 0, 0, 1, 1, 0, 0, 0,
 1, 0, 0, 1, 0, 0, 0, 1, 1, 0, 1, 0, 0, 1, 1};
```

> For Waveform ID 0 this implementation is used to generate 256*8 or 2048 values. The shift
> register is iterated 16 times between the generation of each 8-PSK symbol.

(document page 162, PDF page 167)

```c
int tri( void)
{
   int bitout, bittap, bitin;
   int i,j;

    for(j=0;j<16;j++)
    {
       bitout = bitshift[158];
       bittap = bitshift[31];
       for(i=158;i>=1;i--) bitshift[i]=bitshift[i-1];
       bitin = bitout^bittap;
       bitshift[0]=bitin;
    }
    return (bitshift[2]<<2)+(bitshift[1]<<1)+bitshift[0];
}
```

> Each channel symbol of the WID 0 waveforms is scrambled using 32 chips or symbols of the
> scramble sequence, generated as defined above.
>
> For example, the first 32 symbols of the scramble sequence are
> 5, 6, 2, 1, 7, 3, 1, 1, 6, 0, 5, 4, 0, 7, 7, 0, 5, 3, 1, 3, 3, 2, 2, 5, 5, 4, 7, 3, 5, 4, 3, 0,
>
> For this example, assume the coded and interleaved data di-bit to be sent is 01; then the
> corresponding Walsh sequence 0 4 0 4 is repeated and combined with this scrambling
> sequence as shown below:
> 0, 4, 0, 4, 0, 4, 0, 4, 0, 4, 0, 4, 0, 4, 0, 4, 0, 4, 0, 4, 0, 4, 0, 4, 0, 4, 0, 4, 0, 4, 0, 4
> 5, 6, 2, 1, 7, 3, 1, 1, 6, 0, 5, 4, 0, 7, 7, 0, 5, 3, 1, 3, 3, 2, 2, 5, 5, 4, 7, 3, 5, 4, 3, 0,
> ========================================================
> 5, 2, 2, 5, 7, 7, 1, 5, 6, 4, 5, 0, 0, 3, 7, 4, 5, 7, 1, 7, 3, 6, 2, 1, 5, 0, 7, 7, 5, 0, 3, 4
>
> For the Walsh Orthogonal Modes the sequences are continuously wrapped around the 2048
> symbol boundary. The sequence is reset to the initialization value at the interleaver boundary.

## Table D-XX widPN sync array (document page 170, PDF page 175)

Extracted verbatim into `d20-widpn.txt` (this table also has a text layer).
