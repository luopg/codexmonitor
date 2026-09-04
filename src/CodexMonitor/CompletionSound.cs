using System;
using System.IO;
using System.Linq;
using System.Media;
using System.Text;
using System.Threading.Tasks;

namespace CodexMonitor;

internal static class CompletionSound
{
    private const int SampleRate = 44100;

    private static readonly byte[] WaveData = BuildWave();

    public static void Play()
    {
        Task.Run((Action)PlaySync);
    }

    public static void PlaySync()
    {
        try
        {
            using MemoryStream stream = new MemoryStream(WaveData, writable: false);
            using SoundPlayer soundPlayer = new SoundPlayer(stream);
            soundPlayer.PlaySync();
        }
        catch
        {
            try
            {
                SystemSounds.Exclamation.Play();
            }
            catch
            {
            }
        }
    }

    private static byte[] BuildWave()
    {
        (double, double)[] array = new (double, double)[4]
        {
            (659.25, 0.16),
            (0.0, 0.045),
            (880.0, 0.22),
            (1046.5, 0.28)
        };
        int num = array.Sum(((double Frequency, double Seconds) x) => (int)(44100.0 * x.Seconds));
        using MemoryStream memoryStream = new MemoryStream(44 + num * 2);
        using BinaryWriter binaryWriter = new BinaryWriter(memoryStream);
        binaryWriter.Write(Encoding.ASCII.GetBytes("RIFF"));
        binaryWriter.Write(36 + num * 2);
        binaryWriter.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        binaryWriter.Write(16);
        binaryWriter.Write((short)1);
        binaryWriter.Write((short)1);
        binaryWriter.Write(44100);
        binaryWriter.Write(88200);
        binaryWriter.Write((short)2);
        binaryWriter.Write((short)16);
        binaryWriter.Write(Encoding.ASCII.GetBytes("data"));
        binaryWriter.Write(num * 2);
        (double, double)[] array2 = array;
        for (int i = 0; i < array2.Length; i++)
        {
            (double, double) tuple = array2[i];
            int num2 = (int)(44100.0 * tuple.Item2);
            for (int j = 0; j < num2; j++)
            {
                if (tuple.Item1 == 0.0)
                {
                    binaryWriter.Write((short)0);
                    continue;
                }
                double val = Math.Min(1.0, Math.Min((double)j / 529.2, (double)(num2 - j - 1) / 793.8));
                double num3 = Math.Sin(Math.PI * 2.0 * tuple.Item1 * (double)j / 44100.0) * 0.38 * Math.Max(0.0, val);
                binaryWriter.Write((short)(num3 * 32767.0));
            }
        }
        binaryWriter.Flush();
        return memoryStream.ToArray();
    }
}
