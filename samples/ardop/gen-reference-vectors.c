// ARDOP reference vector generator: compiles ardopcf's actual rrs.c (Rockliff RS)
// alongside verbatim copies of GenCRC16 (ARDOPC.c:1673), GenCRC8 (ARQ.c:200) and
// ComputeTypeParity (ARDOPC.c:1640) extracted below, and emits byte vectors used to
// pin the C# port. Reference: ardopcf git a7c9228 (MIT).
#include <stdio.h>
#include <string.h>
#include <stdbool.h>
#include "rockliff/rrs.h"

typedef unsigned char UCHAR;

// --- verbatim from ardopcf src/common/ARDOPC.c:1673 (GenCRC16) ---
unsigned int GenCRC16(unsigned char * Data, unsigned short length)
{
	int intRegister = 0xffff;
	int i, j;
	int Bit;
	int intPoly = 0x8810;

	for (j = 0; j < length; j++)
	{
		int Mask = 0x80;
		for (i = 0; i < 8; i++)
		{
			Bit = Data[j] & Mask;
			Mask >>= 1;
			if (intRegister & 0x8000)
			{
				if (Bit)
					intRegister = 0xFFFF & (1 + (intRegister << 1));
				else
					intRegister = 0xFFFF & (intRegister << 1);
				intRegister = intRegister ^ intPoly;
			}
			else
			{
				if (Bit)
					intRegister = 0xFFFF & (1 + (intRegister << 1));
				else
					intRegister = 0xFFFF & (intRegister << 1);
			}
		}
	}
	return intRegister;
}

// --- verbatim from ardopcf src/common/ARQ.c:200 (GenCRC8) ---
UCHAR GenCRC8(char * Data)
{
	int intPoly = 0xC6;
	int intRegister  = 0xFF;
	int i;
	unsigned int j;
	int blnBit;

	for (j = 0; j < strlen(Data); j++)
	{
		int Val = Data[j];
		for (i = 7; i >= 0; i--)
		{
			blnBit = (Val & 0x80) != 0;
			Val = Val << 1;
			if ((intRegister & 0x80) == 0x80)
			{
				if (blnBit)
					intRegister = 0xFF & (1 + 2 * intRegister);
				else
					intRegister = 0xFF & (2 * intRegister);
				intRegister = intRegister ^ intPoly;
			}
			else
			{
				if (blnBit)
					intRegister = 0xFF & (1 + 2 * intRegister);
				else
					intRegister = 0xFF & (2 * intRegister);
			}
		}
	}
	return intRegister & 0xFF;
}

// --- verbatim from ardopcf src/common/ARDOPC.c:1640 (ComputeTypeParity) ---
UCHAR ComputeTypeParity(UCHAR bytFrameType)
{
	UCHAR bytMask = 0xC0;
	UCHAR bytParitySum = 1;
	UCHAR bytSym = 0;
	int k;
	for (k = 0; k < 4; k++)
	{
		bytSym = (bytMask & bytFrameType) >> (2 * (3 - k));
		bytParitySum = bytParitySum ^ bytSym;
		bytMask = bytMask >> 2;
	}
	return bytParitySum & 0x3;
}

static void hex(const unsigned char *p, int n) {
	for (int i = 0; i < n; i++) printf("%02X", p[i]);
}

// xorshift PRNG so vectors are reproducible without libc rand() variance
static unsigned int prng_state = 0x12345678;
static unsigned char prng(void) {
	prng_state ^= prng_state << 13;
	prng_state ^= prng_state >> 17;
	prng_state ^= prng_state << 5;
	return (unsigned char)prng_state;
}

int main(void)
{
	int rslen_set[] = {2, 4, 8, 16, 32, 36, 50, 64};
	init_rs(rslen_set, 8);

	// CRC16 vectors: assorted lengths of PRNG data
	int lens[] = {1, 2, 3, 12, 17, 33, 64, 128, 203};
	printf("# crc16 <hexdata> <crc16hex>\n");
	for (unsigned int i = 0; i < sizeof(lens)/sizeof(lens[0]); i++) {
		unsigned char buf[256];
		for (int j = 0; j < lens[i]; j++) buf[j] = prng();
		printf("crc16 "); hex(buf, lens[i]);
		printf(" %04X\n", GenCRC16(buf, lens[i]));
	}
	// Fixed ASCII vector
	printf("crc16 "); hex((const unsigned char*)"ARDOP", 5);
	printf(" %04X\n", GenCRC16((unsigned char*)"ARDOP", 5));

	// CRC8 session-ID vectors
	printf("# crc8 <asciistring> <crc8hex>\n");
	const char *calls[] = {"M7TFFGB7RDG", "M7TFF-3GB7RDG-15", "N0CALLN0CALL", "AB1CDEF-12ZZ9ZZZ-15", "AA"};
	for (unsigned int i = 0; i < sizeof(calls)/sizeof(calls[0]); i++)
		printf("crc8 %s %02X\n", calls[i], GenCRC8((char*)calls[i]));

	// Type parity for every frame type
	printf("# parity <typehex> <parity>\n");
	for (int t = 0; t <= 255; t++)
		printf("parity %02X %d\n", t, ComputeTypeParity((UCHAR)t));

	// RS vectors for the Phase A geometries: (datalen incl count+CRC, rslen)
	// 12/4 = ID/ConReq/Ping; 19/4 = 4FSK.200.50S; 35/8 = 4FSK.500.100S;
	// 67/16 = 4FSK.500.100; 203/50 = 4FSK.2000.600S and each 600 long-frame block.
	int geom[][2] = {{12,4},{19,4},{35,8},{67,16},{203,50}};
	printf("# rs <datalen> <rslen> <hexdata> <hexparity>\n");
	for (unsigned int g = 0; g < sizeof(geom)/sizeof(geom[0]); g++) {
		for (int rep = 0; rep < 3; rep++) {
			unsigned char buf[300];
			int dlen = geom[g][0], rlen = geom[g][1];
			for (int j = 0; j < dlen; j++) buf[j] = prng();
			unsigned char copy[300];
			memcpy(copy, buf, dlen);
			if (rs_append(buf, dlen, rlen) != 0) { printf("rs_append FAILED\n"); return 1; }
			printf("rs %d %d ", dlen, rlen); hex(copy, dlen); printf(" "); hex(buf + dlen, rlen); printf("\n");

			// correction check: corrupt floor(rlen/2) bytes, confirm rs_correct fixes
			unsigned char corrupted[300];
			memcpy(corrupted, buf, dlen + rlen);
			for (int e = 0; e < rlen/2; e++) corrupted[(e * 7) % dlen] ^= (unsigned char)(0x5A + e);
			int n = rs_correct(corrupted, dlen + rlen, rlen, true, false);
			if (n < 0 || memcmp(corrupted, buf, dlen + rlen) != 0) { printf("rs_correct FAILED\n"); return 1; }
		}
	}
	return 0;
}
