using System.Collections.Generic;

namespace Domain.Models;

/// <summary>One row of the Albums grid: two albums side by side.</summary>
public class AlbumRow
{
    public Album? Left { get; set; }
    public Album? Right { get; set; }
}
