// BepInEx 最小桩：只为了让 tests/ 能在"没装游戏、没有 BepInEx.dll"的机器（含 CI）上编译并跑单测。
// 不是 BepInEx 的替代品：只提供 SR.Contracts.cs 实际用到的成员，签名与 BepInEx 5 一致。
using System;
using System.Collections.Generic;

namespace BepInEx.Configuration {
    public class ConfigDefinition {
        public string Section { get; private set; }
        public string Key { get; private set; }
        public ConfigDefinition(string section, string key) { Section = section; Key = key; }
    }

    public class ConfigDescription {
        public string Description { get; private set; }
        public ConfigDescription(string description) { Description = description; }
    }

    public abstract class ConfigEntryBase {
        public ConfigDefinition Definition { get; protected set; }
        public ConfigDescription Description { get; protected set; }
        public ConfigFile ConfigFile { get; protected set; }
        public abstract object BoxedValue { get; set; }
        public abstract object DefaultValue { get; }
        public Type SettingType { get { return DefaultValue != null ? DefaultValue.GetType() : typeof(object); } }
    }

    public class ConfigEntry<T> : ConfigEntryBase {
        private T _value;
        public ConfigEntry(ConfigFile file, string section, string key, T defaultValue, string description) {
            ConfigFile = file;
            Definition = new ConfigDefinition(section, key);
            Description = new ConfigDescription(description);
            _value = defaultValue;
            DefaultValue = defaultValue;
        }
        public T Value { get { return _value; } set { _value = value; } }
        public override object BoxedValue { get { return _value; } set { _value = (T)value; } }
        public override object DefaultValue { get; }
    }

    public class ConfigFile {
        public string ConfigFilePath { get; set; }
        public ConfigFile() { ConfigFilePath = "stub.cfg"; }
        public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, string description) {
            return new ConfigEntry<T>(this, section, key, defaultValue, description);
        }
        public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, ConfigDescription description) {
            return new ConfigEntry<T>(this, section, key, defaultValue, description != null ? description.Description : "");
        }
        public void Save() { }
        public void Reload() { }
    }
}

namespace BepInEx.Logging {
    public class ManualLogSource {
        public void LogInfo(object data) { }
        public void LogWarning(object data) { }
        public void LogError(object data) { }
        public void LogDebug(object data) { }
    }
}
