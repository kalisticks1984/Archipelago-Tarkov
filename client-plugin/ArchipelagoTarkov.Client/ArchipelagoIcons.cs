using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ArchipelagoTarkov.Client
{
    /// <summary>
    /// Lootsanity: draws the Archipelago symbol as the icon of "Archipelago Item" stand-ins. The server mod gives every
    /// stand-in template an id starting with <see cref="StandInPrefix"/>. Tarkov renders item icons from their 3D model
    /// (the stand-ins reuse a plain barter item's model), so the icon the game creates is replaced and kept replaced.
    /// Game types are found by name: if a game update renames them, the plugin logs a warning and the stand-ins simply
    /// keep the base model's icon.
    /// </summary>
    internal static class ArchipelagoIcons
    {
        private const string StandInPrefix = "a9a9a9";
        private const string ResourceName = "ArchipelagoTarkov.Client.Resources.archipelago.png";

        private static readonly ConditionalWeakTable<object, object> Ours = new ConditionalWeakTable<object, object>();
        private static readonly object Marker = new object();
        private static PropertyInfo _stringTemplateId;
        private static MethodInfo _setSprite;
        private static Sprite _sprite;
        private static bool _spriteFailed;
        private static bool _settingOurs;
        private static ManualLogSource _log;

        public static void Install(ManualLogSource log)
        {
            _log = log;
            try
            {
                var creator = AccessTools.TypeByName("ItemIconCreator");
                var icon = AccessTools.TypeByName("ItemIcon");
                var item = AccessTools.TypeByName("EFT.InventoryLogic.Item");
                var getIcon = creator == null ? null : AccessTools.Method(creator, "GetItemIcon");
                _setSprite = icon == null ? null : AccessTools.PropertySetter(icon, "Sprite");
                _stringTemplateId = item == null ? null : AccessTools.Property(item, "StringTemplateId");
                if (getIcon == null || _setSprite == null || _stringTemplateId == null)
                {
                    log.LogWarning("Archipelago: item icon code not found in this game version; Archipelago Items keep their plain icon.");
                    return;
                }

                var harmony = new Harmony(Plugin.Guid + ".icons");
                harmony.Patch(getIcon, postfix: new HarmonyMethod(typeof(ArchipelagoIcons), nameof(GetItemIconPostfix)));
                harmony.Patch(_setSprite, prefix: new HarmonyMethod(typeof(ArchipelagoIcons), nameof(SetSpritePrefix)));
                log.LogInfo("Archipelago: Archipelago Item icons enabled.");
            }
            catch (Exception ex)
            {
                log.LogWarning($"Archipelago: could not hook item icons ({ex.Message}); Archipelago Items keep their plain icon.");
            }
        }

        // ItemIconCreator.GetItemIcon(Item item, ref IntVec2 size, bool ...) -> ItemIcon
        private static void GetItemIconPostfix(object __0, object __result)
        {
            try
            {
                if (__0 == null || __result == null)
                {
                    return;
                }

                if (!(_stringTemplateId.GetValue(__0, null) is string tpl) || !tpl.StartsWith(StandInPrefix, StringComparison.Ordinal))
                {
                    return;
                }

                var sprite = Symbol();
                if (sprite == null)
                {
                    return;
                }

                Ours.Remove(__result);
                Ours.Add(__result, Marker);
                _settingOurs = true;
                try
                {
                    _setSprite.Invoke(__result, new object[] { sprite });
                }
                finally
                {
                    _settingOurs = false;
                }
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"Archipelago: icon swap failed: {ex.Message}");
            }
        }

        // ItemIcon.set_Sprite: the game renders icons in the background and sets them when done; keep ours.
        private static bool SetSpritePrefix(object __instance)
        {
            return _settingOurs || __instance == null || !Ours.TryGetValue(__instance, out _);
        }

        private static Sprite Symbol()
        {
            if (_sprite != null || _spriteFailed)
            {
                return _sprite;
            }

            try
            {
                byte[] bytes;
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
                {
                    if (stream == null)
                    {
                        throw new InvalidOperationException($"{ResourceName} is not embedded");
                    }

                    using (var memory = new MemoryStream())
                    {
                        stream.CopyTo(memory);
                        bytes = memory.ToArray();
                    }
                }

                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(texture, bytes))
                {
                    throw new InvalidOperationException("the image could not be decoded");
                }

                // The source art has a white background: make it transparent so the item's cell colour shows through.
                var pixels = texture.GetPixels32();
                for (var i = 0; i < pixels.Length; i++)
                {
                    if (pixels[i].r > 245 && pixels[i].g > 245 && pixels[i].b > 245)
                    {
                        pixels[i].a = 0;
                    }
                }

                texture.SetPixels32(pixels);
                texture.filterMode = FilterMode.Point; // pixel art: keep it crisp
                texture.Apply(false, true);
                _sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
            }
            catch (Exception ex)
            {
                _spriteFailed = true;
                _log?.LogWarning($"Archipelago: could not load the Archipelago icon ({ex.Message}).");
            }

            return _sprite;
        }
    }
}
