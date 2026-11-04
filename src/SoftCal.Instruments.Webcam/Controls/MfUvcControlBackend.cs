using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Media.DirectShow;
using Windows.Win32.Media.KernelStreaming;

namespace SoftCal.Instruments.Webcam.Controls;

/// <summary>
/// UVC controls through the interfaces the Media Foundation device source exposes: IAMVideoProcAmp and IAMCameraControl
/// for the standard properties, IKsControl for the rest (power-line frequency, auto-exposure priority).
/// </summary>
internal sealed class MfUvcControlBackend : IUvcControlBackend
{
    // Same values for both property sets and for the DirectShow and KS flag enums.
    private const int FlagAuto = 0x1;
    private const int FlagManual = 0x2;

    private readonly IAMVideoProcAmp? _procAmp;
    private readonly IAMCameraControl? _cameraControl;
    private readonly IKsControl? _ks;
    private readonly Lock _gate = new();

    public MfUvcControlBackend(object mediaSource)
    {
        ArgumentNullException.ThrowIfNull(mediaSource);
        _procAmp = mediaSource as IAMVideoProcAmp;
        _cameraControl = mediaSource as IAMCameraControl;
        _ks = mediaSource as IKsControl;
    }

    public bool HasVideoProcAmp => _procAmp is not null;

    public bool HasCameraControl => _cameraControl is not null;

    public bool HasKsControl => _ks is not null;

    public UvcControlRange? GetRange(UvcControlId id)
    {
        lock (_gate)
        {
            try
            {
                if (id.HasDirectShowInterface)
                {
                    int min, max, step, def, caps;
                    if (id.Set == UvcControlSet.VideoProcAmp)
                    {
                        if (_procAmp is null)
                        {
                            return null;
                        }

                        _procAmp.GetRange(id.Property, out min, out max, out step, out def, out caps);
                    }
                    else
                    {
                        if (_cameraControl is null)
                        {
                            return null;
                        }

                        _cameraControl.GetRange(id.Property, out min, out max, out step, out def, out caps);
                    }

                    return new UvcControlRange(min, max, step, def, (caps & FlagAuto) != 0, (caps & FlagManual) != 0);
                }

                // IKsControl does not give a simple range; report the UVC-defined one when the value is readable.
                if (KsGet(id) is not { } current)
                {
                    return null;
                }

                return id == UvcControlCatalog.PowerLineFrequency
                    ? new UvcControlRange(0, 2, 1, current.Value, false, true)
                    : new UvcControlRange(0, 1, 1, current.Value, false, true);
            }
            catch (COMException)
            {
                return null;
            }
        }
    }

    public UvcControlValue? Get(UvcControlId id)
    {
        lock (_gate)
        {
            try
            {
                if (id.HasDirectShowInterface)
                {
                    int value, flags;
                    if (id.Set == UvcControlSet.VideoProcAmp)
                    {
                        if (_procAmp is null)
                        {
                            return null;
                        }

                        _procAmp.Get(id.Property, out value, out flags);
                    }
                    else
                    {
                        if (_cameraControl is null)
                        {
                            return null;
                        }

                        _cameraControl.Get(id.Property, out value, out flags);
                    }

                    return new UvcControlValue(value, (flags & FlagAuto) != 0);
                }

                return KsGet(id);
            }
            catch (COMException)
            {
                return null;
            }
        }
    }

    public void Set(UvcControlId id, UvcControlValue value)
    {
        lock (_gate)
        {
            var flags = value.IsAuto ? FlagAuto : FlagManual;
            if (id.HasDirectShowInterface)
            {
                if (id.Set == UvcControlSet.VideoProcAmp)
                {
                    (_procAmp ?? throw new NotSupportedException("The camera has no IAMVideoProcAmp.")).Set(id.Property, value.Value, flags);
                }
                else
                {
                    (_cameraControl ?? throw new NotSupportedException("The camera has no IAMCameraControl.")).Set(id.Property, value.Value, flags);
                }

                return;
            }

            KsSet(id, value.Value, flags);
        }
    }

    private unsafe UvcControlValue? KsGet(UvcControlId id)
    {
        if (_ks is null)
        {
            return null;
        }

        try
        {
            if (id.Set == UvcControlSet.VideoProcAmp)
            {
                var prop = default(KSPROPERTY_VIDEOPROCAMP_S);
                prop.Property.Set = typeof(PROPSETID_VIDCAP_VIDEOPROCAMP).GUID;
                prop.Property.Id = (uint)id.Property;
                prop.Property.Flags = PInvoke.KSPROPERTY_TYPE_GET;
                _ks.KsProperty((KSIDENTIFIER*)&prop, (uint)sizeof(KSPROPERTY_VIDEOPROCAMP_S), &prop, (uint)sizeof(KSPROPERTY_VIDEOPROCAMP_S), out _);
                return new UvcControlValue(prop.Value, (prop.Flags & FlagAuto) != 0);
            }
            else
            {
                var prop = default(KSPROPERTY_CAMERACONTROL_S);
                prop.Property.Set = typeof(PROPSETID_VIDCAP_CAMERACONTROL).GUID;
                prop.Property.Id = (uint)id.Property;
                prop.Property.Flags = PInvoke.KSPROPERTY_TYPE_GET;
                _ks.KsProperty((KSIDENTIFIER*)&prop, (uint)sizeof(KSPROPERTY_CAMERACONTROL_S), &prop, (uint)sizeof(KSPROPERTY_CAMERACONTROL_S), out _);
                return new UvcControlValue(prop.Value, (prop.Flags & FlagAuto) != 0);
            }
        }
        catch (COMException)
        {
            return null;
        }
    }

    private unsafe void KsSet(UvcControlId id, int value, int flags)
    {
        if (_ks is null)
        {
            throw new NotSupportedException("The camera has no IKsControl.");
        }

        if (id.Set == UvcControlSet.VideoProcAmp)
        {
            var prop = default(KSPROPERTY_VIDEOPROCAMP_S);
            prop.Property.Set = typeof(PROPSETID_VIDCAP_VIDEOPROCAMP).GUID;
            prop.Property.Id = (uint)id.Property;
            prop.Property.Flags = PInvoke.KSPROPERTY_TYPE_SET;
            prop.Value = value;
            prop.Flags = (uint)flags;
            _ks.KsProperty((KSIDENTIFIER*)&prop, (uint)sizeof(KSPROPERTY_VIDEOPROCAMP_S), &prop, (uint)sizeof(KSPROPERTY_VIDEOPROCAMP_S), out _);
        }
        else
        {
            var prop = default(KSPROPERTY_CAMERACONTROL_S);
            prop.Property.Set = typeof(PROPSETID_VIDCAP_CAMERACONTROL).GUID;
            prop.Property.Id = (uint)id.Property;
            prop.Property.Flags = PInvoke.KSPROPERTY_TYPE_SET;
            prop.Value = value;
            prop.Flags = (uint)flags;
            _ks.KsProperty((KSIDENTIFIER*)&prop, (uint)sizeof(KSPROPERTY_CAMERACONTROL_S), &prop, (uint)sizeof(KSPROPERTY_CAMERACONTROL_S), out _);
        }
    }
}
