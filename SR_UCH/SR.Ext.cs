// 扩展点宿主：功能通过七类扩展点挂到核心，核心只负责登记顺序与逐个 try/catch 派发。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class SR {

// 分区：Ext（槽位、UI 阻塞、覆盖层、相机视图、场景 / 收起 / 滚轮钩子）

        // 槽位：名称与形状由功能自定，新增功能不改 SR 文件。
        // 提供方 Provide、使用方 Require，只约定「槽位名 + 委托签名」。
        public static void Provide(string slot, object impl) { Slots.Provide(slot, impl); }
        public static T Require<T>(string slot) where T : class {
            return Slots.Require<T>(slot);
        }

        // 功能 UI 是否打开：SR 据此冻结输入；未注册即无任何功能 UI。
        // 总开关关闭时不能依赖这里收尾：各功能须在每帧回调里判 GateMaster 并还原。
        private static readonly List<Func<bool>> _uiBlockProviders = new List<Func<bool>>();
        public static void RegisterUiBlockProvider(Func<bool> isOpen) {
            if (isOpen != null && !_uiBlockProviders.Contains(isOpen)) _uiBlockProviders.Add(isOpen);
        }
        public static bool AnyUiBlocked() {
            for (int i = 0; i < _uiBlockProviders.Count; i++) {
                try { if (_uiBlockProviders[i]()) return true; }
                catch (Exception __ex) { Guard.Log("查询功能 UI 状态", __ex); }
            }
            return false;
        }

        // 覆盖层绘制：SR 主体绘制完成后调用，各功能自己判断是否该画。
        private static readonly List<Action> _overlays = new List<Action>();
        public static void RegisterOverlay(Action draw) {
            if (draw != null && !_overlays.Contains(draw)) _overlays.Add(draw);
        }
        public static void DrawOverlays() {
            for (int i = 0; i < _overlays.Count; i++) {
                try { _overlays[i](); } catch (Exception __ex) { Guard.Log("绘制功能覆盖层", __ex); }
            }
        }

        private static readonly List<Action<Camera>> _camViews = new List<Action<Camera>>();
        public static void RegisterCameraView(Action<Camera> apply) {
            if (apply != null && !_camViews.Contains(apply)) _camViews.Add(apply);
        }
        public static void ApplyCameraViews(Camera cam) {
            if (cam == null) return;
            for (int i = 0; i < _camViews.Count; i++) {
                try { _camViews[i](cam); } catch (Exception __ex) { Guard.Log("应用相机视图", __ex); }
            }
        }

        private static readonly List<Action> _sceneHooks = new List<Action>();
        public static void RegisterSceneHook(Action onSceneChanged) {
            if (onSceneChanged != null && !_sceneHooks.Contains(onSceneChanged)) _sceneHooks.Add(onSceneChanged);
        }
        public static void FireSceneChanged() {
            // 先取消按键录制：录制态依赖连续 IMGUI 事件，场景一换即断，否则快捷键全失效。
            try { if (IsCapturingKey) CancelCapture(); } catch (Exception __cc) { Guard.Log("切场景取消按键录制", __cc); }
            for (int i = 0; i < _sceneHooks.Count; i++) {
                try { _sceneHooks[i](); } catch (Exception __ex) { Guard.Log("场景切换通知", __ex); }
            }
        }

        private static readonly List<Action> _dismissHooks = new List<Action>();
        public static void RegisterDismissHook(Action dismiss) {
            if (dismiss != null && !_dismissHooks.Contains(dismiss)) _dismissHooks.Add(dismiss);
        }
        public static void FireDismissUi() {
            for (int i = 0; i < _dismissHooks.Count; i++) {
                try { _dismissHooks[i](); } catch (Exception __ex) { Guard.Log("收起功能 UI", __ex); }
            }
        }

        // 滚轮分发：返回 true = 已消费（SR 据此 MarkWheelUsed）。
        private static readonly List<Func<float, bool>> _wheelHooks = new List<Func<float, bool>>();
        public static void RegisterWheelHook(Func<float, bool> onWheel) {
            if (onWheel != null && !_wheelHooks.Contains(onWheel)) _wheelHooks.Add(onWheel);
        }
        public static bool FireWheel(float wheel) {
            bool used = false;
            for (int i = 0; i < _wheelHooks.Count; i++) {
                try { if (_wheelHooks[i](wheel)) used = true; }
                catch (Exception __ex) { Guard.Log("滚轮分发", __ex); }
            }
            return used;
        }

}
}
