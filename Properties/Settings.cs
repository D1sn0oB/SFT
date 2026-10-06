using System.Configuration;

namespace SFTLauncher.Properties
{
    internal sealed partial class Settings : ApplicationSettingsBase
    {
        private static Settings defaultInstance = ((Settings)(ApplicationSettingsBase.Synchronized(new Settings())));

        public static Settings Default => defaultInstance;

        [UserScopedSettingAttribute()]
        [DefaultSettingValueAttribute("BMCL")]
        public string DownloadActiveSource
        {
            get { return ((string)(this["DownloadActiveSource"])); }
            set { this["DownloadActiveSource"] = value; }
        }

        [UserScopedSettingAttribute()]
        [DefaultSettingValueAttribute("32")]
        public int DownloadParallelDownloads
        {
            get { return ((int)(this["DownloadParallelDownloads"])); }
            set { this["DownloadParallelDownloads"] = value; }
        }

        [UserScopedSettingAttribute()]
        [DefaultSettingValueAttribute("")]
        public string DefaultMinecraftPath
        {
            get { return ((string)(this["DefaultMinecraftPath"])); }
            set { this["DefaultMinecraftPath"] = value; }
        }

        [UserScopedSettingAttribute()]
        [DefaultSettingValueAttribute("")]
        public string JavaPath
        {
            get { return ((string)(this["JavaPath"])); }
            set { this["JavaPath"] = value; }
        }

        [UserScopedSettingAttribute()]
        [DefaultSettingValueAttribute("4")]
        public int MaxMemoryGb
        {
            get { return ((int)(this["MaxMemoryGb"])); }
            set { this["MaxMemoryGb"] = value; }
        }
    }
}
