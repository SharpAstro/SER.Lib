namespace SharpAstro.Ser;

/// <summary>
/// SER colour-mode identifier (header field <c>3_ColorID</c>); values match the SER v3 spec.
/// <para>
/// Mono and the Bayer/CYGM mosaics are single-plane raw data that debayers to colour downstream;
/// <see cref="Rgb"/>/<see cref="Bgr"/> are interleaved 3-plane true colour and are a <b>v3</b>
/// extension (a v2 file is mono or Bayer only).
/// </para>
/// </summary>
public enum SerColorId
{
    /// <summary>Greyscale / raw single plane.</summary>
    Mono = 0,

    /// <summary>Bayer mosaic, RGGB phase (upper-left pixel = red).</summary>
    BayerRGGB = 8,
    /// <summary>Bayer mosaic, GRBG phase.</summary>
    BayerGRBG = 9,
    /// <summary>Bayer mosaic, GBRG phase.</summary>
    BayerGBRG = 10,
    /// <summary>Bayer mosaic, BGGR phase.</summary>
    BayerBGGR = 11,

    /// <summary>CYGM-family mosaic, CYYM phase.</summary>
    BayerCYYM = 16,
    /// <summary>CYGM-family mosaic, YCMY phase.</summary>
    BayerYCMY = 17,
    /// <summary>CYGM-family mosaic, YMCY phase.</summary>
    BayerYMCY = 18,
    /// <summary>CYGM-family mosaic, MYYC phase.</summary>
    BayerMYYC = 19,

    /// <summary>Interleaved true colour, byte order R, G, B per pixel (v3).</summary>
    Rgb = 100,
    /// <summary>Interleaved true colour, byte order B, G, R per pixel (v3).</summary>
    Bgr = 101,
}

/// <summary>Convenience queries over <see cref="SerColorId"/>.</summary>
public static class SerColorIdExtensions
{
    extension(SerColorId colorId)
    {
        /// <summary>Colour planes stored per pixel: 3 for RGB/BGR, otherwise 1 (mono / Bayer mosaic).</summary>
        public int PlaneCount => colorId is SerColorId.Rgb or SerColorId.Bgr ? 3 : 1;

        /// <summary>True for the interleaved true-colour modes (<see cref="SerColorId.Rgb"/>/<see cref="SerColorId.Bgr"/>).</summary>
        public bool IsColor => colorId is SerColorId.Rgb or SerColorId.Bgr;

        /// <summary>True for any Bayer/CYGM mosaic (a single raw plane that debayers to colour).</summary>
        public bool IsBayer => colorId is >= SerColorId.BayerRGGB and <= SerColorId.BayerMYYC;

        /// <summary>
        /// CFA offset (x, y) of the red pixel for the RGGB-family Bayer modes, matching the common
        /// debayer convention: RGGB=(0,0), GRBG=(1,0), GBRG=(0,1), BGGR=(1,1). Returns <c>null</c>
        /// for non-RGGB-family modes (mono, the CYGM family, RGB/BGR).
        /// </summary>
        public (int X, int Y)? BayerOffset => colorId switch
        {
            SerColorId.BayerRGGB => (0, 0),
            SerColorId.BayerGRBG => (1, 0),
            SerColorId.BayerGBRG => (0, 1),
            SerColorId.BayerBGGR => (1, 1),
            _ => null,
        };
    }
}
