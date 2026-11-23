# Генерирует icon.ico (многоразмерный, BMP/DIB — максимальная совместимость).
# Запуск: powershell -ExecutionPolicy Bypass -File make_icon.ps1
$ErrorActionPreference = 'Stop'

$cs = @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class MakeIcon
{
    static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Max(0.1f, Math.Min(rad * 2, Math.Min(r.Width, r.Height)));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Bitmap Draw(int s)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);

            float u = s / 32f;
            float bw = 22 * u, bh = 26 * u;
            float bx = (s - 22 * u) / 2f, by = 5 * u;
            var bodyRect = new RectangleF(bx, by, bw, bh);

            // мягкая тень
            var shRect = new RectangleF(bx + 0.8f * u, by + 1.2f * u, bw, bh);
            using (var shPath = Round(shRect, 3.2f * u))
            using (var shBrush = new LinearGradientBrush(shRect, Color.FromArgb(60, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), 90f))
                g.FillPath(shBrush, shPath);

            // корпус
            using (var bodyPath = Round(bodyRect, 3.2f * u))
            {
                using (var bodyBrush = new LinearGradientBrush(bodyRect, Color.FromArgb(248, 248, 251), Color.FromArgb(198, 199, 208), 90f))
                    g.FillPath(bodyBrush, bodyPath);
                using (var pen = new Pen(Color.FromArgb(40, 255, 255, 255), Math.Max(1f, u)))
                    g.DrawPath(pen, bodyPath);
            }

            // зажим сверху
            float cw = 11 * u, ch = 5.5f * u;
            var clipRect = new RectangleF((s - 11 * u) / 2f, 5 * u - ch / 2f, cw, ch);
            using (var clipPath = Round(clipRect, 2.2f * u))
            using (var clipBrush = new LinearGradientBrush(clipRect, Color.FromArgb(160, 162, 174), Color.FromArgb(108, 110, 122), 90f))
                g.FillPath(clipBrush, clipPath);

            // линии текста
            using (var pen = new Pen(Color.FromArgb(120, 60, 60, 68), Math.Max(1f, 1.6f * u)))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                float x0 = 5 * u + bx, x1 = s - 10 * u;
                float ly = 10.5f * u + by;
                g.DrawLine(pen, x0, ly, x1, ly);
                g.DrawLine(pen, x0, ly + 5.2f * u, x1, ly + 5.2f * u);
                g.DrawLine(pen, x0, ly + 10.4f * u, x1 - 3 * u, ly + 10.4f * u);
            }
        }
        return bmp;
    }

    static byte[] ToDib(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var ms = new MemoryStream();
        var wr = new BinaryWriter(ms);
        wr.Write((uint)40);
        wr.Write((int)w);
        wr.Write((int)(h * 2));
        wr.Write((ushort)1);
        wr.Write((ushort)32);
        wr.Write((uint)0);
        wr.Write((uint)(w * h * 4));
        wr.Write((int)0);
        wr.Write((int)0);
        wr.Write((uint)0);
        wr.Write((uint)0);
        for (int y = h - 1; y >= 0; y--)
            for (int x = 0; x < w; x++)
            {
                var c = bmp.GetPixel(x, y);
                wr.Write((byte)c.B);
                wr.Write((byte)c.G);
                wr.Write((byte)c.R);
                wr.Write((byte)c.A);
            }
        int rowBytes = ((w + 31) / 32) * 4;
        wr.Write(new byte[rowBytes * h]);
        wr.Flush();
        return ms.ToArray();
    }

    public static void Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "icon.ico";
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        var imgs = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++)
            using (var b = Draw(sizes[i]))
                imgs[i] = ToDib(b);

        using (var fs = File.Create(outPath))
        using (var wr = new BinaryWriter(fs))
        {
            wr.Write((ushort)0);
            wr.Write((ushort)1);
            wr.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                byte wb = (byte)(s >= 256 ? 0 : s);
                wr.Write(wb); wr.Write(wb);
                wr.Write((byte)0); wr.Write((byte)0);
                wr.Write((ushort)1); wr.Write((ushort)32);
                wr.Write((uint)imgs[i].Length);
                wr.Write((uint)offset);
                offset += imgs[i].Length;
            }
            foreach (var im in imgs) wr.Write(im);
        }
        Console.WriteLine("icon.ico -> " + Path.GetFullPath(outPath));
    }
}
'@

Add-Type -TypeDefinition $cs -ReferencedAssemblies System.Drawing

$out = Join-Path $PSScriptRoot 'icon.ico'
[MakeIcon]::Main(@($out))

if ($args.Count -gt 0 -and $args[0] -eq 'preview') {
    $bmp = [MakeIcon]::Draw(256)
    $bmp.Save((Join-Path $PSScriptRoot 'icon_preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}