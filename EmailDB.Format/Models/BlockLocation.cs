namespace EmailDB.Format.Models;

/// <summary>
/// Represents the position and length of a block within the data file.
/// </summary>
public struct BlockLocation
{
    public long Position { get; set; }
    public long Length { get; set; }
}