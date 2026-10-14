using System.Windows.Media.Imaging;
using QRCoder;

namespace LinkTwo.UI;

public partial class PairingWindow
{
    public PairingWindow(string pairingUri, string computerName)
    {
        InitializeComponent();
        Subtitle.Text = $"Open LinkTwo on your phone, tap “Scan pairing code”, and point it here. " +
                        $"The phone needs to be on the same Wi-Fi as “{computerName}”.";
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(pairingUri, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(10);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(png);
        bmp.EndInit();
        Qr.Source = bmp;
    }
}
