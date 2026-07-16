/* Oracle harness for the pdn-soundmodem FreeDV OFDM modulator.
 *
 * Drives codec2 1.2.0 (git 310777b) ofdm_mod for a set of datac modes with a deterministic
 * payload (the seed-1 LCG, ofdm_generate_payload_data_bits), and writes reference TX vectors:
 *   <mode>_packet.s16        full clipped+BPF packet, real part, int16 (== freedv_rawdatatx)
 *   <mode>_packet_nobpf.s16  same but tx_bpf_en=false (pre-BPF stage-2 checkpoint), int16
 *   <mode>_preamble_raw.f32  raw preamble frame (amp_scale=1, no clip/bpf), interleaved re,im
 *   <mode>_meta.txt          bits_per_packet, samples_per_packet/frame, tx_nlower, uw_ind_sym[]
 *
 * Build:  gcc oracle_harness.c -I codec2-ref/src -I codec2-ref/build/src \
 *              -L codec2-ref/build/src -lcodec2 -lm -o oracle_harness
 * Run  :  LD_LIBRARY_PATH=codec2-ref/build/src ./oracle_harness <outdir>
 */
#include <assert.h>
#include <complex.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "codec2_ofdm.h"
#include "comp.h"
#include "ofdm_internal.h"

static struct OFDM *make(const char *mode) {
  struct OFDM_CONFIG *cfg = calloc(1, sizeof(struct OFDM_CONFIG));
  assert(cfg != NULL);
  ofdm_init_mode((char *)mode, cfg);
  struct OFDM *ofdm = ofdm_create(cfg);
  assert(ofdm != NULL);
  free(cfg);
  return ofdm;
}

static void write_s16_real(const char *path, COMP *x, int n) {
  FILE *f = fopen(path, "wb");
  assert(f != NULL);
  for (int i = 0; i < n; i++) {
    short s = (short)x[i].real; /* C float->short truncation, as freedv_rawdatatx does */
    fwrite(&s, sizeof(short), 1, f);
  }
  fclose(f);
}

static void write_f32_complex(const char *path, COMP *x, int n) {
  FILE *f = fopen(path, "wb");
  assert(f != NULL);
  for (int i = 0; i < n; i++) {
    float re = x[i].real, im = x[i].imag;
    fwrite(&re, sizeof(float), 1, f);
    fwrite(&im, sizeof(float), 1, f);
  }
  fclose(f);
}

static void run_mode(const char *mode, const char *outdir) {
  char path[512];

  struct OFDM *a = make(mode);
  int bpp = ofdm_get_bits_per_packet(a);
  int spp = ofdm_get_samples_per_packet(a);
  int spf = ofdm_get_samples_per_frame(a);

  /* deterministic payload = seed-1 LCG (reproduced in C# as LcgBits(n,1)) */
  uint8_t *pbits = malloc(bpp);
  ofdm_generate_payload_data_bits(pbits, bpp);
  int *bits = malloc(sizeof(int) * bpp);
  for (int i = 0; i < bpp; i++) bits[i] = pbits[i];

  /* (1) full packet, with clipper + BPF, fresh struct */
  COMP *pkt = malloc(sizeof(COMP) * spp);
  ofdm_mod(a, pkt, bits);
  snprintf(path, sizeof(path), "%s/%s_packet.s16", outdir, mode);
  write_s16_real(path, pkt, spp);

  /* (3) raw preamble frame (built at create time, amp_scale=1/no clip/no bpf) */
  snprintf(path, sizeof(path), "%s/%s_preamble_raw.f32", outdir, mode);
  write_f32_complex(path, a->tx_preamble, spf);

  /* meta / structural checkpoints */
  snprintf(path, sizeof(path), "%s/%s_meta.txt", outdir, mode);
  FILE *m = fopen(path, "w");
  assert(m != NULL);
  fprintf(m, "mode %s\n", mode);
  fprintf(m, "bits_per_packet %d\n", bpp);
  fprintf(m, "samples_per_packet %d\n", spp);
  fprintf(m, "samples_per_frame %d\n", spf);
  fprintf(m, "tx_nlower %g\n", (double)a->tx_nlower);
  fprintf(m, "nuwbits %d\n", a->nuwbits);
  fprintf(m, "uw_ind_sym");
  for (int i = 0; i < a->nuwbits / a->bps; i++) fprintf(m, " %d", a->uw_ind_sym[i]);
  fprintf(m, "\n");
  fclose(m);

  free(pkt);
  ofdm_destroy(a);

  /* (2) no-BPF variant on a fresh struct (zero filter state) */
  struct OFDM *b = make(mode);
  b->tx_bpf_en = false;
  COMP *pkt2 = malloc(sizeof(COMP) * spp);
  ofdm_mod(b, pkt2, bits);
  snprintf(path, sizeof(path), "%s/%s_packet_nobpf.s16", outdir, mode);
  write_s16_real(path, pkt2, spp);
  free(pkt2);
  ofdm_destroy(b);

  free(pbits);
  free(bits);
  printf("%-8s bpp=%d spp=%d spf=%d\n", mode, bpp, spp, spf);
}

int main(int argc, char **argv) {
  const char *outdir = argc > 1 ? argv[1] : ".";
  const char *modes[] = {"datac0", "datac1", "datac3", "datac14"};
  for (int i = 0; i < 4; i++) run_mode(modes[i], outdir);
  return 0;
}
