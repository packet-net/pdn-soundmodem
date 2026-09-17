// The bytes Cm108Ptt puts on the wire, against a fake HIDDevice. Node's own test runner, no
// dependencies, because the package has none and is not about to acquire one for this.
//
// This suite exists for one reason: the native class carried the GPIO data and direction bytes
// the wrong way round from the day it was written until 2026-09-17, and nothing caught it,
// because a keyup writes the same value into both and is identical either way. Only the
// release tells the two orderings apart. Assert on the unkey or this passes while swapped.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { Cm108Ptt } from '../src/modem.js'

/** Stands in for the one HID call the class makes, keeping every report it was sent. */
class FakeHidDevice {
  constructor() {
    this.opened = true
    this.vendorId = 0x0d8c
    this.productId = 0x013c
    this.productName = 'USB PnP Sound Device'
    this.reports = []
    this.closed = false
  }

  async sendReport(reportId, data) { this.reports.push([reportId, [...data]]) }
  async close() { this.closed = true }
}

// The class registers a pagehide handler; node has no window, so give it the two functions.
globalThis.addEventListener ??= () => {}
globalThis.removeEventListener ??= () => {}

test('a keyup drives the pin high with the direction register set to output', async () => {
  const device = new FakeHidDevice()
  await new Cm108Ptt(device, 3).key()

  assert.deepEqual(device.reports, [[0x00, [0x00, 0x04, 0x04, 0x00]]])
})

test('an unkey drives the pin low rather than turning it back into an input', async () => {
  const device = new FakeHidDevice()
  const ptt = new Cm108Ptt(device, 3)
  await ptt.key()
  await ptt.unkey()

  // The whole point of the file: data 0x00 with the direction register still 0x04. Swapped,
  // this reads [0x04, 0x00], which floats the pin instead of pulling it down, and on a board
  // with no gate pull-down the radio stays keyed.
  assert.deepEqual(device.reports.at(-1), [0x00, [0x00, 0x00, 0x04, 0x00]])
})

test('closing releases the radio before the device goes', async () => {
  const device = new FakeHidDevice()
  const ptt = new Cm108Ptt(device, 3)
  await ptt.key()
  await ptt.close()

  assert.deepEqual(device.reports.at(-1), [0x00, [0x00, 0x00, 0x04, 0x00]])
  assert.equal(device.closed, true)
})

test('the pin number selects one bit of the gpio register', async () => {
  for (const [gpio, bit] of [[1, 0x01], [3, 0x04], [8, 0x80]]) {
    const device = new FakeHidDevice()
    await new Cm108Ptt(device, gpio).key()

    assert.deepEqual(device.reports.at(-1), [0x00, [0x00, bit, bit, 0x00]])
  }
})

test('a pin outside the chip is refused', () => {
  for (const gpio of [0, 9, 3.5]) {
    assert.throws(() => new Cm108Ptt(new FakeHidDevice(), gpio), /gpio must be 1 to 8/)
  }
})

test('a failing report names the device, not just the write', async () => {
  const device = new FakeHidDevice()
  const ptt = new Cm108Ptt(device, 3)
  device.sendReport = async () => { throw new Error('The device was disconnected.') }

  // "Failed to write the report" on its own does not say which of the two things plugged in
  // stopped answering, which is the question being asked when a station goes quiet.
  await assert.rejects(() => ptt.key(), /USB PnP Sound Device: The device was disconnected\./)
})

test('the debug hook traces every report', async () => {
  const lines = []
  const ptt = new Cm108Ptt(new FakeHidDevice(), 3, (message) => lines.push(message))
  await ptt.key()

  assert.match(lines[0], /opened USB PnP Sound Device \(0d8c:013c\), keying GPIO3 \(mask 0x04\)/)
  assert.match(lines.at(-1), /key\s+-> report 0x00 \[00 04 04 00\]/)
})
