// Generates app.ico (multi-size clipboard-with-image icon).
// Build & run: see build.cmd (runs automatically when app.ico is missing).

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

internal static class MakeIcon
{
    private static void Main(string[] args)
    {
        string outputPath = args.Length > 0 ? args[0] : "app.ico";
        int[] sizes = new int[] { 16, 32, 48, 256 };
        byte[][] entries = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++)
        {
            using (Bitmap bmp = Draw(sizes[i]))
            {
                entries[i] = ToIconEntry(bmp);
            }
        }

        using (FileStream fs = File.Create(outputPath))
        using (BinaryWriter bw = new BinaryWriter(fs))
        {
            bw.Write((ushort)0);                 // reserved
            bw.Write((ushort)1);                 // type: icon
            bw.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                byte dim = sizes[i] >= 256 ? (byte)0 : (byte)sizes[i];
                bw.Write(dim);                   // width
                bw.Write(dim);                   // height
                bw.Write((byte)0);               // color count
                bw.Write((byte)0);               // reserved
                bw.Write((ushort)1);             // color planes
                bw.Write((ushort)32);            // bits per pixel
                bw.Write((uint)entries[i].Length);
                bw.Write((uint)offset);
                offset += entries[i].Length;
            }
            for (int i = 0; i < sizes.Length; i++) bw.Write(entries[i]);
        }
        Console.WriteLine("Created: " + Path.GetFullPath(outputPath));
    }

    // 256x256 design: white clipboard with a blue photo window, scaled down to each size
    private static Bitmap Draw(int size)
    {
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = size / 256f;
            g.ScaleTransform(s, s);

            using (GraphicsPath body = RoundedRect(44, 58, 168, 172, 20))
            {
                g.FillPath(Brushes.White, body);
                using (Pen pen = new Pen(Color.FromArgb(255, 62, 84, 128), 13f))
                {
                    g.DrawPath(pen, body);
                }
            }

            using (GraphicsPath clip = RoundedRect(100, 34, 56, 54, 13))
            {
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(255, 116, 130, 150)))
                {
                    g.FillPath(brush, clip);
                }
            }

            using (GraphicsPath window = RoundedRect(66, 90, 124, 104, 10))
            {
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(255, 51, 156, 244)))
                {
                    g.FillPath(brush, window);
                }
            }

            using (SolidBrush sun = new SolidBrush(Color.FromArgb(255, 255, 210, 72)))
            {
                g.FillEllipse(sun, 84, 102, 32, 32);
            }

            PointF[] mountain = new PointF[]
            {
                new PointF(76, 192),
                new PointF(118, 132),
                new PointF(146, 168),
                new PointF(162, 150),
                new PointF(186, 192)
            };
            g.FillPolygon(Brushes.White, mountain);
        }
        return bmp;
    }

    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        GraphicsPath p = new GraphicsPath();
        float d = 2f * r;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ICO entry: BITMAPINFOHEADER + bottom-up 32bpp BGRA pixels + AND mask
    private static byte[] ToIconEntry(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        Rectangle rect = new Rectangle(0, 0, w, h);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride = data.Stride;
        byte[] pixels = new byte[stride * h];
        Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        bmp.UnlockBits(data);

        int rowLen = w * 4;
        byte[] xor = new byte[rowLen * h];
        for (int y = 0; y < h; y++)
        {
            Array.Copy(pixels, (h - 1 - y) * stride, xor, y * rowLen, rowLen);
        }

        int maskRow = ((w + 31) / 32) * 4;
        byte[] andMask = new byte[maskRow * h];

        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter bw = new BinaryWriter(ms))
        {
            bw.Write((uint)40);                          // biSize
            bw.Write((int)w);                            // biWidth
            bw.Write((int)(h * 2));                      // biHeight (XOR + AND)
            bw.Write((ushort)1);                         // biPlanes
            bw.Write((ushort)32);                        // biBitCount
            bw.Write((uint)0);                           // biCompression
            bw.Write((uint)(xor.Length + andMask.Length));
            bw.Write((int)0);                            // biXPelsPerMeter
            bw.Write((int)0);                            // biYPelsPerMeter
            bw.Write((uint)0);                           // biClrUsed
            bw.Write((uint)0);                           // biClrImportant
            bw.Write(xor);
            bw.Write(andMask);
            bw.Flush();
            return ms.ToArray();
        }
    }
}
