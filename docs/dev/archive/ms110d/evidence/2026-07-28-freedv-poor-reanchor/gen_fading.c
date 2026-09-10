/* gen_fading.c — generate codec2 `ch`-compatible HF fading files without Octave.
 *
 * codec2's channel simulator `ch` (src/ch.c) reads a two-path fading file produced by the Octave
 * function ch_fading.m -> doppler_spread.m:
 *
 *     sigma = dopplerSpreadHz/2;
 *     y = (1/(sigma*sqrt(2*pi)))*exp(-(f.^2)/(2*sigma*sigma));   % Gaussian magnitude response
 *     b = fir2(99, f/(Fs/2), y);                                 % FIR with that response
 *     spread = filter(b, 1, complex_white_gaussian);            % one per path
 *     hf_gain = 1/sqrt(var(spread)+var(spread_2ms));
 *
 * i.e. each of the two paths is complex white Gaussian noise shaped by a filter whose MAGNITUDE
 * response is a Gaussian of standard deviation sigma = dopplerSpreadHz/2 Hz. This program produces a
 * statistically-identical process by the equivalent spectral-synthesis method: draw complex-Gaussian
 * Fourier coefficients directly at Fs, multiply each by that same Gaussian magnitude response
 * exp(-f^2/(2*sigma^2)), and inverse-FFT. That reproduces doppler_spread's *specified* Doppler
 * spectrum exactly (it is defined by y), differing from Octave only in fir2/resample numerics — which
 * the end-to-end anchor (codec2 tx -> ch(this file) -> codec2 rx reproducing the published N/100)
 * validates away. Each path is normalised to unit variance; hf_gain follows ch_fading's definition.
 *
 * Output float32 layout matches ch_fading exactly:
 *   [hf_gain, hf_gain, hf_gain, hf_gain,  then per sample: re(p0), im(p0), re(p1), im(p1), ...]
 *
 * Build:  cc -O2 -o gen_fading gen_fading.c -lm
 * Usage:  ./gen_fading <outfile> <Fs> <dopplerSpreadHz> <durationSecs> <seed>
 *   MPP (Multipath Poor): dopplerSpreadHz 1.0, delay 2 ms (delay is `ch`'s, not ours)
 *   MPG (Multipath Good): dopplerSpreadHz 0.1, delay 0.5 ms
 */

#include <complex.h>
#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

/* iterative radix-2 Cooley-Tukey; sign=-1 forward, +1 inverse (unnormalised) */
static void fft(double complex *a, size_t n, int sign) {
  for (size_t i = 1, j = 0; i < n; i++) {
    size_t bit = n >> 1;
    for (; j & bit; bit >>= 1) j ^= bit;
    j ^= bit;
    if (i < j) {
      double complex t = a[i];
      a[i] = a[j];
      a[j] = t;
    }
  }
  for (size_t len = 2; len <= n; len <<= 1) {
    double ang = sign * 2.0 * M_PI / (double)len;
    double complex wlen = cos(ang) + I * sin(ang);
    for (size_t i = 0; i < n; i += len) {
      double complex w = 1.0;
      for (size_t k = 0; k < len / 2; k++) {
        double complex u = a[i + k];
        double complex v = a[i + k + len / 2] * w;
        a[i + k] = u + v;
        a[i + k + len / 2] = u - v;
        w *= wlen;
      }
    }
  }
}

/* Box-Muller unit-variance-per-component complex Gaussian */
static double complex cgauss(void) {
  double u1 = (rand() + 1.0) / (RAND_MAX + 2.0);
  double u2 = (rand() + 1.0) / (RAND_MAX + 2.0);
  double u3 = (rand() + 1.0) / (RAND_MAX + 2.0);
  double u4 = (rand() + 1.0) / (RAND_MAX + 2.0);
  double re = sqrt(-2.0 * log(u1)) * cos(2.0 * M_PI * u2);
  double im = sqrt(-2.0 * log(u3)) * cos(2.0 * M_PI * u4);
  return re + I * im;
}

/* Synthesise one unit-variance complex fading path with a Gaussian Doppler magnitude response of
 * standard deviation sigma (Hz), length nSam at sample rate Fs. Returns var (should be ~1). */
static double synth_path(double complex *out, size_t nSam, double Fs, double sigma) {
  size_t M = 1;
  while (M < nSam) M <<= 1;
  double complex *spec = malloc(M * sizeof(double complex));
  if (!spec) {
    fprintf(stderr, "gen_fading: OOM (M=%zu)\n", M);
    exit(1);
  }
  for (size_t k = 0; k < M; k++) {
    double f = (k <= M / 2) ? (double)k * Fs / M : ((double)k - (double)M) * Fs / M;
    double h = exp(-(f * f) / (2.0 * sigma * sigma)); /* codec2's y = Gaussian magnitude response */
    spec[k] = cgauss() * h;
  }
  fft(spec, M, +1); /* inverse */
  /* measure variance over the used samples, normalise to unit variance */
  double p = 0;
  for (size_t i = 0; i < nSam; i++) p += creal(spec[i]) * creal(spec[i]) + cimag(spec[i]) * cimag(spec[i]);
  double var = p / (double)nSam; /* E[|x|^2] */
  double scale = 1.0 / sqrt(var);
  for (size_t i = 0; i < nSam; i++) out[i] = spec[i] * scale;
  free(spec);
  return 1.0; /* normalised */
}

int main(int argc, char **argv) {
  if (argc < 6) {
    fprintf(stderr, "usage: %s <outfile> <Fs> <dopplerSpreadHz> <durationSecs> <seed>\n", argv[0]);
    return 1;
  }
  const char *outfile = argv[1];
  double Fs = atof(argv[2]);
  double doppler = atof(argv[3]);
  double dur = atof(argv[4]);
  unsigned seed = (unsigned)atoi(argv[5]);
  srand(seed);

  size_t nSam = (size_t)(Fs * dur);
  double sigma = doppler / 2.0; /* doppler_spread.m: sigma = dopplerSpreadHz/2 */

  double complex *p0 = malloc(nSam * sizeof(double complex));
  double complex *p1 = malloc(nSam * sizeof(double complex));
  if (!p0 || !p1) {
    fprintf(stderr, "gen_fading: OOM\n");
    return 1;
  }
  synth_path(p0, nSam, Fs, sigma);
  synth_path(p1, nSam, Fs, sigma);

  /* both paths unit variance -> hf_gain = 1/sqrt(1+1); compute from realised var to match ch_fading */
  double v0 = 0, v1 = 0;
  for (size_t i = 0; i < nSam; i++) {
    v0 += creal(p0[i]) * creal(p0[i]) + cimag(p0[i]) * cimag(p0[i]);
    v1 += creal(p1[i]) * creal(p1[i]) + cimag(p1[i]) * cimag(p1[i]);
  }
  v0 /= nSam;
  v1 /= nSam;
  float hf_gain = (float)(1.0 / sqrt(v0 + v1));

  FILE *f = fopen(outfile, "wb");
  if (!f) {
    fprintf(stderr, "gen_fading: cannot open %s\n", outfile);
    return 1;
  }
  for (int i = 0; i < 4; i++) fwrite(&hf_gain, sizeof(float), 1, f);
  for (size_t i = 0; i < nSam; i++) {
    float v[4] = {(float)creal(p0[i]), (float)cimag(p0[i]), (float)creal(p1[i]), (float)cimag(p1[i])};
    fwrite(v, sizeof(float), 4, f);
  }
  fclose(f);
  free(p0);
  free(p1);

  fprintf(stderr, "gen_fading: %s Fs=%.0f doppler=%.2fHz sigma=%.3fHz dur=%.0fs nSam=%zu hf_gain=%f\n",
          outfile, Fs, doppler, sigma, dur, nSam, hf_gain);
  return 0;
}
