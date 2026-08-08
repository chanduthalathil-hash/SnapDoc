using System;
using System.Runtime.InteropServices;

namespace SnapDoc.Recording;

/// <summary>
/// Picture-in-picture placement + blit math, called once per frame by <see cref="MfScreenRecorder"/>
/// (see its class doc) to bake the webcam directly into the screen frame's own pixel buffer live,
/// during recording. There's no separate webcam track and nothing left for <see cref="VideoEditExporter"/>
/// to composite at export time -- by the time a recording reaches the editor/exporter, the webcam is
/// already just part of the video's pixels, same as any other frame content.
/// </summary>
internal static class WebcamCompositor
{
    public static void Composite(nint screenPtr, int screenStride, int screenWidth, int screenHeight,
        byte[] camBgr32, int camWidth, int camHeight, double anchorX, double anchorY, double sizeFraction)
    {
        if (camWidth <= 0 || camHeight <= 0) return;

        const int BorderPx = 2;
        int pipWidth = Math.Clamp((int)(screenWidth * sizeFraction), 80, screenWidth - 20);
        int pipHeight = Math.Clamp(pipWidth * camHeight / camWidth, 1, Math.Max(1, screenHeight - 20));
        int margin = Math.Max(12, screenWidth / 100);

        // anchorX/Y (0..1) place the box's top-left anywhere within the margin-inset area, same
        // convention as WebcamPositionPicker / the editor's overlay drag handle uses.
        int minX = margin, maxX = Math.Max(minX, screenWidth - pipWidth - margin);
        int minY = margin, maxY = Math.Max(minY, screenHeight - pipHeight - margin);
        int pipX = minX + (int)((maxX - minX) * Math.Clamp(anchorX, 0, 1));
        int pipY = minY + (int)((maxY - minY) * Math.Clamp(anchorY, 0, 1));

        int camStride = camWidth * 4;
        var rowBuffer = new byte[pipWidth * 4];

        for (int y = 0; y < pipHeight; y++)
        {
            bool horizontalBorder = y < BorderPx || y >= pipHeight - BorderPx;
            int srcY = Math.Min(camHeight - 1, y * camHeight / pipHeight);
            int srcRowStart = srcY * camStride;

            for (int x = 0; x < pipWidth; x++)
            {
                int dstOffset = x * 4;
                if (horizontalBorder || x < BorderPx || x >= pipWidth - BorderPx)
                {
                    rowBuffer[dstOffset] = 255; rowBuffer[dstOffset + 1] = 255;
                    rowBuffer[dstOffset + 2] = 255; rowBuffer[dstOffset + 3] = 255;
                }
                else
                {
                    int srcOffset = srcRowStart + Math.Min(camWidth - 1, x * camWidth / pipWidth) * 4;
                    rowBuffer[dstOffset] = camBgr32[srcOffset];
                    rowBuffer[dstOffset + 1] = camBgr32[srcOffset + 1];
                    rowBuffer[dstOffset + 2] = camBgr32[srcOffset + 2];
                    rowBuffer[dstOffset + 3] = 255;
                }
            }

            nint destRowPtr = screenPtr + (pipY + y) * screenStride + pipX * 4;
            Marshal.Copy(rowBuffer, 0, destRowPtr, rowBuffer.Length);
        }
    }
}
