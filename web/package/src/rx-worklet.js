// The receive end of the audio graph. This runs on the audio render thread, where the .NET
// runtime cannot go (an AudioWorklet has no module loader and no threads of its own), so all
// it does is gather render quanta into blocks big enough to be worth a message and hand them
// over. The DSP happens where the runtime is.
//
// A block is one allocation per ~21 ms at 48 kHz. That is the price of not having
// SharedArrayBuffer: a shared ring would need COOP/COEP headers on the page, which static
// hosting often will not set, and a packet modem does not need the microseconds it would buy.
class SoundModemRx extends AudioWorkletProcessor {
  constructor(options) {
    super()
    this.blockSize = options?.processorOptions?.blockSize ?? 1024
    this.block = new Float32Array(this.blockSize)
    this.at = 0
  }

  process(inputs) {
    const channel = inputs[0]?.[0]
    if (!channel) return true
    for (let i = 0; i < channel.length; i++) {
      this.block[this.at++] = channel[i]
      if (this.at === this.blockSize) {
        this.port.postMessage(this.block, [this.block.buffer])
        this.block = new Float32Array(this.blockSize)
        this.at = 0
      }
    }
    return true
  }
}

registerProcessor('sound-modem-rx', SoundModemRx)
