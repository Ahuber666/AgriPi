using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ObjectDetectionApp.Utilities;

public static class SoftwareBitmapExtensions
{
    public static byte[] ToBgra8Bytes(this SoftwareBitmap bitmap)
    {
        if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || bitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
        {
            using var converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            return ExtractBytes(converted);
        }

        return ExtractBytes(bitmap);
    }

    public static WriteableBitmap ToWriteableBitmap(this byte[] pixels, int width, int height)
    {
        if (pixels is null)
        {
            throw new ArgumentNullException(nameof(pixels));
        }

        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        var expectedLength = width * height * 4;
        if (pixels.Length < expectedLength)
        {
            throw new ArgumentException("Pixel buffer is smaller than expected for the supplied dimensions.", nameof(pixels));
        }

        var writeableBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        writeableBitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        return writeableBitmap;
    }

    private static byte[] ExtractBytes(SoftwareBitmap bitmap)
    {
        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        using var reference = buffer.CreateReference();
        return GetBytes(reference, bitmap.PixelWidth * bitmap.PixelHeight * 4);
    }

    private static unsafe byte[] GetBytes(Windows.Foundation.IMemoryBufferReference reference, int length)
    {
        if (reference is null)
        {
            throw new ArgumentNullException(nameof(reference));
        }

        if (reference is not IMemoryBufferByteAccess byteAccess)
        {
            throw new InvalidCastException("Unable to access memory buffer.");
        }

        byteAccess.GetBuffer(out byte* dataInBytes, out uint capacity);
        var bytes = new byte[Math.Min(length, (int)capacity)];
        Marshal.Copy(new IntPtr(dataInBytes), bytes, 0, bytes.Length);
        return bytes;
    }

    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        unsafe void GetBuffer(out byte* buffer, out uint capacity);
    }
}
