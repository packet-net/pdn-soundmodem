// The receive end of the audio graph. This runs on the audio render thread, where the .NET
// runtime cannot go (an AudioWorklet has no module loader and no threads of its own), so all
// it does is gather render quanta into blocks big enough to be worth a message and hand them
// over, with the block's own level beside it. The DSP happens where the runtime is.
//
// A block is one allocation per ~21 ms at 48 kHz. That is the price of not having
// SharedArrayBuffer: a shared ring would need COOP/COEP headers on the page, which static
// hosting often will not set, and a packet modem does not need the microseconds it would buy.
//
// The level is measured HERE, and on the same samples the modem is about to be given - which
// is to say after the receive gain, because that node sits between the microphone and this
// one. A meter that read the input before its own gain control would move the bar when the
// signal moved and not when the slider did, which is the one thing it exists to show.
class SoundModemRx extends AudioWorkletProcessor {
  constructor(options) {
    super()
    this.blockSize = options?.processorOptions?.blockSize ?? 1024
    this.block = new Float32Array(this.blockSize)
    this.at = 0
    this.peak = 0
    this.sumSquares = 0
  }

  process(inputs) {
    const channel = inputs[0]?.[0]
    if (!channel) return true
    for (let i = 0; i < channel.length; i++) {
      const sample = channel[i]
      this.block[this.at++] = sample
      const magnitude = Math.abs(sample)
      if (magnitude > this.peak) this.peak = magnitude
      this.sumSquares += sample * sample
      if (this.at === this.blockSize) {
        // Linear here, dBFS on the other side: a logarithm per block is nothing, but the
        // floor a zero block needs is a display decision and this is not the display.
        this.port.postMessage({
          block: this.block,
          peak: this.peak,
          rms: Math.sqrt(this.sumSquares / this.blockSize),
        }, [this.block.buffer])
        this.block = new Float32Array(this.blockSize)
        this.at = 0
        this.peak = 0
        this.sumSquares = 0
      }
    }
    return true
  }
}

registerProcessor('sound-modem-rx', SoundModemRx)
