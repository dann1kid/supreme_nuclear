using System;
using System.Collections.Generic;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;


namespace SupremeNuclear
{
    // Часть SupcomUi: система плиток 48×48: Tile/бейджи/прогресс, кнопки-хелперы.
    // Расщепление без изменения логики (2026-11-07): механический перенос
    // членов в partial-файл; канвас собирает SupcomUi.cs (BuildSafe).
    public sealed partial class SupcomUi
    {
        /// <summary>Спека плитки 48×48 (консолидация v0.48): каталог/очереди/ростер —
        /// иконка, короткая подпись (code), бейдж ×N/цена (право-верх), бейдж типа
        /// объекта (лево-верх), прогресс-бар, клики ЛКМ/ПКМ, hover-карточка (Def).</summary>
        private struct TileSpec
        {
            public Sprite Icon;
            public string Caption;      // короткая подпись юнита (низ плитки)
            public string Badge;        // ×N стака или цена каталога (право-верх)
            public float? Progress;     // полоса готовности головной позиции очереди
            public Color? BarColor;
            public string TypeBadge;    // тип объекта: ТНК/ЗРК/АРТ... или И/У/М авиации
            public string TierBadge;    // тир ростера каталога: T1/T2/T3 (право-низ, §2.13)
            public System.Action OnLeft;
            public System.Action OnRight;
            public UnitDefinition Def;  // не null — плитка регистрируется для hover-карточки
            public bool WheelLoadout;    // каталог авиации: колесо над плиткой циклит пресет лодаута
        }

        /// <summary>ЕДИНЫЙ строитель плитки 48×48 (консолидация v0.48): иконка,
        /// опционально бейдж ×N и прогресс-бар; клики ЛКМ/ПКМ через TileClicks.</summary>
        private GameObject Tile(Transform parent, in TileSpec spec)
        {
            var go = new GameObject("tile", typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.sizeDelta = new Vector2(48f, 48f);
            go.GetComponent<Image>().color = PanelBg2;
            var le = go.AddComponent<LayoutElement>();
            le.minWidth = 48f;
            le.minHeight = 48f;
            le.preferredWidth = 48f;
            le.preferredHeight = 48f;

            var iconImg = Icon(go.transform, spec.Icon, new Vector2(30f, 30f));
            if (spec.Def != null && spec.Icon != null)
                UnitPreview.Bind(iconImg, spec.Def); // плитка не пересобралась сама — превью подменится по готовности
            ((RectTransform)iconImg.transform).anchorMin = ((RectTransform)iconImg.transform).anchorMax =
                new Vector2(0.5f, 1f);
            ((RectTransform)iconImg.transform).anchoredPosition = new Vector2(0f, -18f);

            if (!string.IsNullOrEmpty(spec.TypeBadge))
                AddTileTypeBadge(go.transform, spec.TypeBadge);
            if (!string.IsNullOrEmpty(spec.TierBadge))
            {
                var tb = Label(go.transform, "tier", spec.TierBadge, 10, Accent, TextAnchor.LowerRight);
                var rtT = (RectTransform)tb.transform;
                rtT.anchorMin = new Vector2(0.45f, 0f);
                rtT.anchorMax = new Vector2(0.98f, 0.28f);
                rtT.offsetMin = rtT.offsetMax = Vector2.zero;
                tb.raycastTarget = false;
            }
            if (!string.IsNullOrEmpty(spec.Badge))
            {
                var b = Label(go.transform, "n", spec.Badge, 11, Accent, TextAnchor.UpperRight);
                var rtN = (RectTransform)b.transform;
                rtN.anchorMin = new Vector2(0.35f, 0.62f);
                rtN.anchorMax = new Vector2(0.97f, 0.98f);
                rtN.offsetMin = rtN.offsetMax = Vector2.zero;
                b.raycastTarget = false;
            }
            bool hasCaption = !string.IsNullOrEmpty(spec.Caption);
            if (hasCaption)
            {
                var c = Label(go.transform, "cap", spec.Caption, 10, TextMain, TextAnchor.LowerCenter);
                var rtC = (RectTransform)c.transform;
                rtC.anchorMin = new Vector2(0.02f, 0f);
                rtC.anchorMax = new Vector2(0.98f, 0.26f);
                rtC.offsetMin = rtC.offsetMax = Vector2.zero;
                c.raycastTarget = false;
            }
            if (spec.Progress.HasValue)
                AddTileProgressBar(go.transform, spec.Progress.Value,
                    spec.BarColor.HasValue && spec.BarColor.Value.r > 0.9f, hasCaption);

            var tile = go.AddComponent<TileClicks>();
            tile.Left = spec.OnLeft;
            tile.Right = spec.OnRight;
            if (spec.Def != null)
                _tipTiles.Add((rt, parent.parent as RectTransform, spec.Def, spec.WheelLoadout));
            return go;
        }

        /// <summary>Бейдж типа в левом-верхнем углу плитки: цветная аббревиатура
        /// на полупрозрачной подложке — различает однотипные квадратики каталога.</summary>
        private void AddTileTypeBadge(Transform tile, string badge)
        {
            float w = Mathf.Clamp(8f + 5.5f * badge.Length, 14f, 32f);
            var bg = new GameObject("type", typeof(RectTransform), typeof(Image));
            var brt = (RectTransform)bg.transform;
            brt.SetParent(tile, false);
            brt.anchorMin = brt.anchorMax = new Vector2(0f, 1f);
            brt.pivot = new Vector2(0f, 1f);
            brt.anchoredPosition = new Vector2(2f, -1f);
            brt.sizeDelta = new Vector2(w, 12f);
            var bimg = bg.GetComponent<Image>();
            bimg.color = new Color(0f, 0f, 0f, 0.55f);
            bimg.raycastTarget = false;
            var t = Label(bg.transform, "lbl", badge, 9, BadgeColorOf(badge), TextAnchor.MiddleCenter);
            var rtT = (RectTransform)t.transform;
            rtT.anchorMin = Vector2.zero;
            rtT.anchorMax = Vector2.one;
            rtT.offsetMin = rtT.offsetMax = Vector2.zero;
            t.raycastTarget = false;
        }

        /// <summary>Цвет бейджа типа: класс техники/роли авиации кодируется цветом
        /// (броня — зелёный, ПВО — фиолетовый, артиллерия — оранжевый и т.д.).
        /// Цепочка if, а не switch: паттерны switch — только константы, вызов
        /// Loc.Tr в позиции case не компилируется (CS0426).</summary>
        private static Color BadgeColorOf(string badge)
        {
            if (badge == Loc.Tr("И")) return new Color(0.45f, 0.85f, 1f, 1f);
            if (badge == Loc.Tr("У")) return new Color(1f, 0.68f, 0.3f, 1f);
            if (badge == Loc.Tr("М")) return new Color(1f, 0.9f, 0.35f, 1f);
            if (badge == Loc.Tr("ТР")) return new Color(0.72f, 0.78f, 0.84f, 1f);
            if (badge == Loc.Tr("РЛС")) return new Color(0.62f, 0.9f, 1f, 1f);
            if (badge == Loc.Tr("ТНК") || badge == Loc.Tr("ББМ") || badge == Loc.Tr("ЛБМ"))
                return new Color(0.55f, 0.85f, 0.5f, 1f);
            if (badge == Loc.Tr("АРТ")) return new Color(1f, 0.65f, 0.35f, 1f);
            if (badge == Loc.Tr("ЗУ") || badge == Loc.Tr("ПЗРК") || badge == Loc.Tr("ЗРК"))
                return new Color(0.85f, 0.55f, 1f, 1f);
            return new Color(0.72f, 0.76f, 0.72f, 1f);
        }

        /// <summary>Иконка плитки (v0.153): 3D-превью модели (UnitPreview, пока рендерится —
        /// фолбэк); без модели — авиация силуэт mapIcon, прочее — визорный friendlyIcon.</summary>
        private static Sprite IconOf(UnitDefinition def)
        {
            if (def == null)
                return null;
            Sprite preview = UnitPreview.PreviewOf(def);
            if (preview != null)
                return preview;
            if (def is AircraftDefinition && def.mapIcon != null)
                return def.mapIcon;
            return def.friendlyIcon != null ? def.friendlyIcon : def.mapIcon;
        }

        /// <summary>Бейдж типа плитки: наземка — из vehicleType, флот — класс
        /// корабля, авиация — роль из roleIdentity; здания — без бейджа.</summary>
        private static string TypeBadgeOf(UnitDefinition def)
        {
            if (def is VehicleDefinition vd)
                return Catalog.BadgeOf(vd.vehicleType);
            if (def is ShipDefinition ship)
                return Catalog.BadgeOf(ship.shipType);
            if (def is AircraftDefinition)
                return Catalog.AirBadge(def.roleIdentity, def.typeIdentity);
            return null;
        }

        /// <summary>Клики плитки: ЛКМ — действие, ПКМ — правое действие (наборы/очереди).</summary>
        private sealed class TileClicks : MonoBehaviour, IPointerDownHandler
        {
            public System.Action Left;
            public System.Action Right;

            public void OnPointerDown(PointerEventData e)
            {
                if (e.button == PointerEventData.InputButton.Left)
                    Left?.Invoke();
                else if (e.button == PointerEventData.InputButton.Right)
                    Right?.Invoke();
            }
        }

        /// <summary>Прогресс-бар в нижней части плитки очереди (заполнение = готовность);
        /// при наличии подписи поднимается над ней.</summary>
        private void AddTileProgressBar(Transform tile, float progress, bool blocked, bool hasCaption)
        {
            var track = new GameObject("pb", typeof(RectTransform), typeof(Image));
            var trt = (RectTransform)track.transform;
            trt.SetParent(tile, false);
            if (hasCaption)
            {
                trt.anchorMin = new Vector2(0.06f, 0.3f);
                trt.anchorMax = new Vector2(0.94f, 0.44f);
            }
            else
            {
                trt.anchorMin = new Vector2(0.06f, 0.03f);
                trt.anchorMax = new Vector2(0.94f, 0.16f);
            }
            trt.offsetMin = trt.offsetMax = Vector2.zero;
            track.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.5f);
            track.GetComponent<Image>().raycastTarget = false;

            var fill = new GameObject("f", typeof(RectTransform), typeof(Image));
            var frt = (RectTransform)fill.transform;
            frt.SetParent(trt, false);
            frt.anchorMin = Vector2.zero;
            frt.anchorMax = new Vector2(Mathf.Clamp01(progress), 1f);
            frt.offsetMin = frt.offsetMax = Vector2.zero;
            var fimg = fill.GetComponent<Image>();
            fimg.color = blocked
                ? new Color(1f, 0.45f, 0.3f, 0.95f)
                : new Color(0.35f, 1f, 0.5f, 0.95f);
            fimg.raycastTarget = false;
        }

        /// <summary>Позиция каталога с иконкой — плитка Tile с подписью-именем;
        /// без иконки (шаблоны, подсказки) — текстовая пилюля.</summary>
        private void AddItemButton(Transform parent, Sprite sprite, string label, System.Action onClick)
        {
            if (sprite != null)
            {
                Tile(parent, new TileSpec
                {
                    Icon = sprite,
                    Caption = TrimCaption(label),
                    OnLeft = onClick,
                });
                return;
            }
            AddTextPill(parent, label, onClick);
        }

        /// <summary>Подпись плитки-шаблона: имя без переносов, до 10 символов.</summary>
        private static string TrimCaption(string s)
        {
            if (string.IsNullOrEmpty(s))
                return null;
            s = s.Replace('\n', ' ');
            return s.Length <= 10 ? s : s.Substring(0, 9) + "…";
        }

        /// <summary>Текстовая пилюля — для шаблонов и подсказок (без иконки).</summary>
        private void AddTextPill(Transform parent, string label, System.Action onClick)
        {
            var go = new GameObject("item", typeof(RectTransform), typeof(Image), typeof(Button));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.sizeDelta = new Vector2(178f, 30f);
            var le = go.AddComponent<LayoutElement>();
            le.minWidth = 178f;
            le.preferredWidth = 178f;
            le.minHeight = 30f;
            le.preferredHeight = 30f;
            go.GetComponent<Image>().color = onClick != null
                ? UiKit.BtnBg
                : new Color(0.08f, 0.10f, 0.12f, 0.9f);
            var btn = go.GetComponent<Button>();
            if (onClick != null)
                btn.onClick.AddListener(() => onClick());

            var txt = Label(go.transform, "lbl", label, 12,
                onClick != null ? TextMain : TextDim, TextAnchor.MiddleLeft);
            var rtT = (RectTransform)txt.transform;
            rtT.anchorMin = new Vector2(0.04f, 0f);
            rtT.anchorMax = new Vector2(1f, 1f);
            rtT.offsetMin = rtT.offsetMax = Vector2.zero;
            txt.raycastTarget = false;
        }
    }
}
