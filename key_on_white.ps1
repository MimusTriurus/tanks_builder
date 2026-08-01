param(
    [string]$In,
    [string]$Out,
    [string]$Preview,
    [int]$Cols = 4,
    [int]$Rows = 4,
    [int]$PaperMin = 250,
    [int]$DilateRadius = 20,
    [int]$BlurRadius = 12,
    [int]$WarmRadius = 16,
    [double]$WarmLo = 18.0,
    [double]$WarmHi = 58.0)

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

public static class Key3
{
    // Lifts emissive art drawn on white paper into straight RGBA.
    //
    // Three things defeat the obvious "alpha from distance to white" key, and
    // each one cost an attempt.
    //
    // 1. The hot core of a flash is overexposed, so it is near-white, so colour
    //    alone cannot tell it from the paper. Flooding the paper inwards from
    //    the border - the trick already written down for slicing hollow shells -
    //    says which white is enclosed, but a pixel at 242 is still keyed to
    //    alpha 13 and the core comes out as a hole.
    //
    // 2. What actually marks the core is shape: a dip in alpha surrounded by
    //    high alpha. Filling it with a morphological closing works, but a
    //    separable square element fills with axis-aligned rectangles, and the
    //    smoke ends up covered in visible blocks. Dilating and then blurring
    //    fills the same dip without the corners.
    //
    // 3. Applied everywhere, that fill also inflates every pale patch of smoke
    //    into an opaque blob. Brightness cannot gate it either: measured, the
    //    centre of the core is (254,254,242) and pale smoke is (251,251,249) -
    //    both are white. What separates them is warmth, and not their own: the
    //    core reads R-B of only 12, while the material around it reads 50 to
    //    200. Pale smoke has no warm surroundings at all. So the gate is the
    //    warmth of the neighbourhood, blurred, and it fades in rather than
    //    switching, so no edge of it can show.
    public static void Run(string inPath, string outPath, string previewPath,
                           int cols, int rows, int paperMin,
                           int dilateRadius, int blurRadius, int warmRadius,
                           double warmLo, double warmHi)
    {
        var src = new Bitmap(inPath);
        int w = src.Width, h = src.Height;
        var rs = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var p = new byte[rs.Stride * h];
        System.Runtime.InteropServices.Marshal.Copy(rs.Scan0, p, 0, p.Length);
        src.UnlockBits(rs);

        int cw = w / cols, ch = h / rows;
        var outp = new byte[rs.Stride * h];

        for (int k = 0; k < cols * rows; k++)
        {
            int ox = (k % cols) * cw, oy = (k / cols) * ch;
            var alpha = new double[cw * ch];
            var warm = new double[cw * ch];
            var paper = new bool[cw * ch];

            var queue = new Queue<int>();
            for (int x = 0; x < cw; x++) { Seed(p, rs.Stride, paper, ox, oy, cw, x, 0, paperMin, queue);
                                           Seed(p, rs.Stride, paper, ox, oy, cw, x, ch - 1, paperMin, queue); }
            for (int y = 0; y < ch; y++) { Seed(p, rs.Stride, paper, ox, oy, cw, 0, y, paperMin, queue);
                                           Seed(p, rs.Stride, paper, ox, oy, cw, cw - 1, y, paperMin, queue); }
            while (queue.Count > 0)
            {
                int at = queue.Dequeue();
                int x = at % cw, y = at / cw;
                if (x > 0)      Seed(p, rs.Stride, paper, ox, oy, cw, x - 1, y, paperMin, queue);
                if (x < cw - 1) Seed(p, rs.Stride, paper, ox, oy, cw, x + 1, y, paperMin, queue);
                if (y > 0)      Seed(p, rs.Stride, paper, ox, oy, cw, x, y - 1, paperMin, queue);
                if (y < ch - 1) Seed(p, rs.Stride, paper, ox, oy, cw, x, y + 1, paperMin, queue);
            }

            for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
            {
                int i = (oy + y) * rs.Stride + (ox + x) * 4;
                int at = y * cw + x;
                int R = p[i+2], B = p[i];
                int min = Math.Min(p[i], Math.Min(p[i+1], p[i+2]));
                alpha[at] = paper[at] ? 0.0 : 255.0 - min;
                warm[at] = paper[at] ? 0.0 : Math.Max(0, R - B);
            }

            double[] fill = Blur(Blur(MaxFilter(alpha, cw, ch, dilateRadius), cw, ch, blurRadius), cw, ch, blurRadius);
            double[] support = Blur(Blur(warm, cw, ch, warmRadius), cw, ch, warmRadius);

            // How much paper is in the neighbourhood. Filling is for holes, and
            // a hole is enclosed by definition; without this the fill leaks
            // outward through the flame's own soft edge, where the surroundings
            // are still warm but the pixel is nearly paper already, and hangs a
            // white halo on the silhouette.
            var paperMask = new double[cw * ch];
            for (int j = 0; j < paperMask.Length; j++) paperMask[j] = paper[j] ? 1.0 : 0.0;
            double[] nearPaper = Blur(paperMask, cw, ch, dilateRadius);

            for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
            {
                int i = (oy + y) * rs.Stride + (ox + x) * 4;
                int at = y * cw + x;
                if (paper[at]) { outp[i] = outp[i+1] = outp[i+2] = outp[i+3] = 0; continue; }

                double t = (support[at] - warmLo) / (warmHi - warmLo);
                t = t < 0.0 ? 0.0 : (t > 1.0 ? 1.0 : t);
                double enclosed = (0.12 - nearPaper[at]) / 0.12;
                t *= enclosed < 0.0 ? 0.0 : (enclosed > 1.0 ? 1.0 : enclosed);
                double a = alpha[at] + t * Math.Max(0.0, fill[at] - alpha[at]);
                if (a > 255.0) a = 255.0;
                if (a <= 0.0) { outp[i] = outp[i+1] = outp[i+2] = outp[i+3] = 0; continue; }

                double al = Math.Max(a / 255.0, 0.02);
                for (int c = 0; c < 3; c++)
                {
                    double v = (p[i + c] - 255.0 * (1.0 - al)) / al;
                    outp[i + c] = (byte)(v < 0.0 ? 0.0 : (v > 255.0 ? 255.0 : v));
                }
                outp[i + 3] = (byte)a;
            }
        }

        var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var rd = dst.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        System.Runtime.InteropServices.Marshal.Copy(outp, 0, rd.Scan0, outp.Length);
        dst.UnlockBits(rd);
        dst.Save(outPath, ImageFormat.Png);
        Console.WriteLine("wrote " + outPath);

        var prev = new Bitmap(cw * cols, ch * rows * 2, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(prev))
        {
            g.Clear(Color.FromArgb(255, 168, 171, 163));
            g.FillRectangle(Brushes.Black, 0, ch * rows, cw * cols, ch * rows);
            g.DrawImage(dst, new Rectangle(0, 0, cw * cols, ch * rows), 0, 0, w, h, GraphicsUnit.Pixel);
            g.DrawImage(dst, new Rectangle(0, ch * rows, cw * cols, ch * rows), 0, 0, w, h, GraphicsUnit.Pixel);
        }
        prev.Save(previewPath, ImageFormat.Png);
        Console.WriteLine("wrote " + previewPath);
    }

    private static double[] MaxFilter(double[] a, int w, int h, int r)
    {
        return Sweep(Sweep(a, w, h, r, true), w, h, r, false);
    }

    private static double[] Sweep(double[] a, int w, int h, int r, bool horizontal)
    {
        var o = new double[a.Length];
        int outer = horizontal ? h : w, inner = horizontal ? w : h;
        for (int u = 0; u < outer; u++)
        for (int i = 0; i < inner; i++)
        {
            double best = double.MinValue;
            int lo = Math.Max(0, i - r), hi = Math.Min(inner - 1, i + r);
            for (int j = lo; j <= hi; j++)
            {
                double v = horizontal ? a[u * w + j] : a[j * w + u];
                if (v > best) best = v;
            }
            if (horizontal) o[u * w + i] = best; else o[i * w + u] = best;
        }
        return o;
    }

    private static double[] Blur(double[] a, int w, int h, int r)
    {
        var mid = new double[a.Length];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            double s = 0; int n = 0;
            int lo = Math.Max(0, x - r), hi = Math.Min(w - 1, x + r);
            for (int j = lo; j <= hi; j++) { s += a[y * w + j]; n++; }
            mid[y * w + x] = s / n;
        }
        var o = new double[a.Length];
        for (int x = 0; x < w; x++)
        for (int y = 0; y < h; y++)
        {
            double s = 0; int n = 0;
            int lo = Math.Max(0, y - r), hi = Math.Min(h - 1, y + r);
            for (int j = lo; j <= hi; j++) { s += mid[j * w + x]; n++; }
            o[y * w + x] = s / n;
        }
        return o;
    }

    private static void Seed(byte[] p, int stride, bool[] paper, int ox, int oy,
                             int cw, int x, int y, int paperMin, Queue<int> queue)
    {
        int at = y * cw + x;
        if (paper[at]) return;
        int i = (oy + y) * stride + (ox + x) * 4;
        if (Math.Min(p[i], Math.Min(p[i+1], p[i+2])) < paperMin) return;
        paper[at] = true;
        queue.Enqueue(at);
    }
}
"@

[Key3]::Run($In, $Out, $Preview, $Cols, $Rows, $PaperMin, $DilateRadius, $BlurRadius, $WarmRadius, $WarmLo, $WarmHi)
