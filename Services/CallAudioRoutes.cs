using Maui.WebRTC;

namespace Mi_nU.Services
{
    /// <summary>A place a call can be heard: speakers, headphones, the phone's earpiece, a Bluetooth headset.</summary>
    public sealed record AudioOutput(string Id, string Name);

    /// <summary>
    /// Where a call is heard. On Windows the choice goes to libwebrtc's audio device and is remembered for later calls;
    /// on Android it goes to AudioManager and lasts for the call, since what is plugged in changes from call to call.
    /// </summary>
    static class CallAudioRoutes
    {
#if WINDOWS
        const string RememberedKey = "audio_output";
        /// <summary>The Windows entry that follows the system's default communications device.</summary>
        const string SystemDefault = "";
#endif

        /// <summary>Puts the device in call mode; <paramref name="speaker"/> starts on the loudspeaker, as video calls do.</summary>
        public static void Enter(PeerConnectionFactory factory, bool speaker)
        {
#if ANDROID
            // Without this mode Android applies no echo cancellation and keeps the output at media volume.
            var audio = Manager();
            audio.Mode = global::Android.Media.Mode.InCommunication;
#pragma warning disable CA1422 // Still honoured on every version we support; the replacement is used for an explicit choice.
            audio.SpeakerphoneOn = speaker;
#pragma warning restore CA1422
#elif WINDOWS
            var remembered = Preferences.Default.Get(RememberedKey, SystemDefault);
            if (remembered != SystemDefault)
            {
                var device = factory.AudioOutputs.FirstOrDefault(output => output.Name == remembered);
                // Unplugged since: the system's choice it is, until the user picks again.
                factory.SetAudioOutput(device);
            }
#endif
        }

        public static void Leave()
        {
#if ANDROID
            var audio = Manager();
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
                audio.ClearCommunicationDevice();
#pragma warning disable CA1422
            audio.SpeakerphoneOn = false;
#pragma warning restore CA1422
            audio.Mode = global::Android.Media.Mode.Normal;
#endif
        }

        public static IReadOnlyList<AudioOutput> List(PeerConnectionFactory? factory)
        {
#if ANDROID
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
                return CommunicationDevices();
            return [new AudioOutput(Earpiece, "Телефон"), new AudioOutput(Speaker, "Громкая связь")];
#elif WINDOWS
            if (factory is null)
                return [];
            return [new AudioOutput(SystemDefault, "Как в системе"), .. factory.AudioOutputs.Select(device => new AudioOutput(device.Name, device.Name))];
#else
            return [];
#endif
        }

        /// <summary>The <see cref="AudioOutput.Id"/> in use, as far as the system tells.</summary>
        public static string? Current(PeerConnectionFactory? factory)
        {
#if ANDROID
            var audio = Manager();
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
                return audio.CommunicationDevice?.Id.ToString();
#pragma warning disable CA1422
            return audio.SpeakerphoneOn ? Speaker : Earpiece;
#pragma warning restore CA1422
#elif WINDOWS
            _ = factory;
            return Preferences.Default.Get(RememberedKey, SystemDefault);
#else
            return null;
#endif
        }

        public static bool Select(PeerConnectionFactory? factory, AudioOutput output)
        {
#if ANDROID
            var audio = Manager();
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                var device = audio.AvailableCommunicationDevices.FirstOrDefault(device => device.Id.ToString() == output.Id);
                return device is not null && audio.SetCommunicationDevice(device);
            }
#pragma warning disable CA1422
            audio.SpeakerphoneOn = output.Id == Speaker;
#pragma warning restore CA1422
            return true;
#elif WINDOWS
            if (factory is null)
                return false;
            var device = output.Id == SystemDefault ? null : factory.AudioOutputs.FirstOrDefault(d => d.Name == output.Id);
            if (output.Id != SystemDefault && device is null)
                return false;
            if (!factory.SetAudioOutput(device))
                return false;
            Preferences.Default.Set(RememberedKey, output.Id);
            return true;
#else
            return false;
#endif
        }

#if ANDROID
        const string Earpiece = "earpiece";
        const string Speaker = "speaker";

        static global::Android.Media.AudioManager Manager() =>
            (global::Android.Media.AudioManager)global::Android.App.Application.Context
                .GetSystemService(global::Android.Content.Context.AudioService)!;

        [System.Runtime.Versioning.SupportedOSPlatform("android31.0")]
        static List<AudioOutput> CommunicationDevices() =>
            Manager().AvailableCommunicationDevices
                .Select(device => (device, name: NameOf(device)))
                .Where(x => x.name is not null)
                // A headset can be listed once per profile; one entry each is enough.
                .DistinctBy(x => x.name)
                .Select(x => new AudioOutput(x.device.Id.ToString(), x.name!))
                .ToList();

        [System.Runtime.Versioning.SupportedOSPlatform("android31.0")]
        static string? NameOf(global::Android.Media.AudioDeviceInfo device) => device.Type switch
        {
            global::Android.Media.AudioDeviceType.BuiltinEarpiece => "Телефон",
            global::Android.Media.AudioDeviceType.BuiltinSpeaker => "Громкая связь",
            global::Android.Media.AudioDeviceType.WiredHeadset or global::Android.Media.AudioDeviceType.WiredHeadphones
                or global::Android.Media.AudioDeviceType.UsbHeadset => "Проводные наушники",
            global::Android.Media.AudioDeviceType.BluetoothSco or global::Android.Media.AudioDeviceType.BleHeadset
                or global::Android.Media.AudioDeviceType.BleSpeaker =>
                string.IsNullOrWhiteSpace(device.ProductName) ? "Bluetooth" : $"Bluetooth · {device.ProductName}",
            global::Android.Media.AudioDeviceType.HearingAid => "Слуховой аппарат",
            _ => null,
        };
#endif
    }
}
