using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ben.Datasync.Server
{
    [Table("Users")]
    public class UserRecord
    {
        [Key]
        public Guid UserId { get; set; }

        [Required, MaxLength(200)]
        public string ExternalId { get; set; } = string.Empty;

        [Required, MaxLength(50)]
        public string IdentityProvider { get; set; } = string.Empty;

        [MaxLength(200)]
        public string? Email { get; set; }

        public DateTime CreatedAt { get; set; }
    }
}
