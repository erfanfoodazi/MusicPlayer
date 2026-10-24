using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Models
{
    public class PlaylistTrack
    {
        public int Id { get; set; }
        public int PlaylistId { get; set; }
        public int MusicId { get; set; }
        public int Order { get; set; }
        public DateTime AddedAt { get; set; } = DateTime.UtcNow;

        public Playlist? Playlist { get; set; }
        public Music? Music { get; set; }
    }
}