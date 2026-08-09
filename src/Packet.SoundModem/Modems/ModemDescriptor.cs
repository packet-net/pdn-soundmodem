namespace Packet.SoundModem.Modems;

/// <summary>
/// What a plugin tells the catalogue about one mode it can build: everything
/// <see cref="ModemCatalog"/> can be asked about a mode, minus the factory.
/// </summary>
/// <remarks>
/// <para>Deliberately a separate type from the catalogue's own private descriptor rather than that
/// one made public. The internal row carries a <c>Func</c> factory closed over library-private
/// types, a centre-semantics enum whose <c>Shifted</c> member means "wrap this in
/// <see cref="FrequencyShiftedModem"/>", and a NinoTNC ident flag that is a statement about
/// somebody else's hardware. None of that is a plugin's business, and publishing it would freeze
/// the built-in catalogue's shape into the plugin ABI, so that adding a field for the built-ins
/// would break every plugin in the world. This type is the smaller thing that actually has to
/// cross the boundary; <see cref="ModemCatalog"/> adapts between them.</para>
/// <para>Descriptor names are the mode's name <em>inside its plugin</em> - <c>nb</c>, not
/// <c>ofdm-fm:nb</c>. The catalogue prefixes them with the plugin's
/// <see cref="IModemPlugin.Id"/>, which is what stops a plugin shadowing a built-in mode, and a
/// plugin is asked to build the same bare name it declared.</para>
/// </remarks>
/// <param name="Name">The mode's name within its plugin. Must be non-empty and must not contain
/// <c>:</c>, which is the catalogue's plugin separator.</param>
/// <param name="DspRate">The channel DSP rate this mode's chain runs at, in Hz. A host's capture
/// rate must be an integer multiple of it; the daemon's shared channel runs at 12000 or 48000, so
/// a mode declaring anything else is refused there by name rather than quietly resampled.</param>
/// <param name="AcceptsCentre">Whether the mode has a settable audio-centre frequency. False means
/// it occupies the band from DC upwards like the built-in <c>fsk*</c> family, and supplying a
/// centre for it throws.</param>
/// <param name="DefaultCentreHz">The centre used when no override is given. Must be set when
/// <paramref name="AcceptsCentre"/> is true and null when it is false: those are the same fact
/// stated twice, and the catalogue refuses a descriptor that states it two different ways rather
/// than picking one and being quietly wrong on a dial frequency.</param>
/// <param name="RunsIl2pCrc">Whether the mode's receiver runs IL2P+CRC, and so has something for
/// <see cref="ModemOptions.AcceptPlainIl2p"/> to switch on. False refuses that option, on the same
/// reasoning the built-ins use: an operator who set it believes their modem got more tolerant.</param>
public sealed record ModemDescriptor(
    string Name,
    int DspRate,
    bool AcceptsCentre,
    double? DefaultCentreHz,
    bool RunsIl2pCrc)
{
    /// <summary>Checks a descriptor is self-consistent, throwing with the reason.</summary>
    /// <exception cref="ArgumentException">The name is empty or contains <c>:</c>, the DSP rate is
    /// not positive, or the centre declarations contradict each other.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("a mode descriptor needs a name", nameof(Name));
        }

        if (Name.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"mode name '{Name}' contains ':', which the catalogue uses to separate a plugin "
                + "id from its mode - name the mode without it and the id supplies the prefix",
                nameof(Name));
        }

        if (DspRate <= 0)
        {
            throw new ArgumentException(
                $"mode '{Name}' declares a DSP rate of {DspRate} Hz", nameof(DspRate));
        }

        if (AcceptsCentre && DefaultCentreHz is null)
        {
            throw new ArgumentException(
                $"mode '{Name}' accepts a centre frequency but declares no default one - a host "
                + "that has to place it in a passband has nothing to place",
                nameof(DefaultCentreHz));
        }

        if (!AcceptsCentre && DefaultCentreHz is not null)
        {
            throw new ArgumentException(
                $"mode '{Name}' declares a default centre of {DefaultCentreHz} Hz but says it "
                + "accepts no centre frequency - one of the two is wrong",
                nameof(DefaultCentreHz));
        }
    }
}
