using PSQLite;

namespace PrinterServices.Data.Models
{
    [Table("config_settings")]
    public class ConfigSettingEntity
    {
        [PrimaryKey]
        [Column("key")]
        public string Key { get; set; }

        [Column("value")]
        public string Value { get; set; }

        [Column("default_value")]
        public string DefaultValue { get; set; }

        [Column("description")]
        public string Description { get; set; }

        [Column("category")]
        public string Category { get; set; }

        [Column("value_type")]
        public string ValueType { get; set; }

        [Column("min_value")]
        public string MinValue { get; set; }

        [Column("max_value")]
        public string MaxValue { get; set; }

        [Column("updated_at")]
        public string UpdatedAt { get; set; }
    }
}
