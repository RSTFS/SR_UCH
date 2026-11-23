// 扩展点宿主：功能通过七类扩展点挂到核心，核心只负责登记顺序与逐个 try/catch 派发。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class SR {

// 分区：Ext（UI 阻塞、覆盖层、相机视图、场景 / 收起 / 滚轮钩子）

        // 功能 UI 是否打开：SR 据此冻结输入；未注册即无任何功能 UI。
        // 总开关关闭时不能依赖这里收尾：各功能须在每帧回调里判 GateMaster 并还原。
        private static readonly List<Func<bool>> _uiBlockProviders = new List<Func<bool>>();
        public static void RegisterUiBlockProvider(Func<bool> isOpen) {
            if (isOpen != null && !_uiBlockProviders.Contains(isOpen)) _uiBlockProviders.Add(isOpen);
            FeatureRuntime.RecordRevoke(() => _uiBlockProviders.Remove(isOpen));
            FeatureRuntime.RecordSubscription("RegisterUiBlockProvider");
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
            FeatureRuntime.RecordRevoke(() => _overlays.Remove(draw));
            FeatureRuntime.RecordSubscription("RegisterOverlay");
        }
        public static void DrawOverlays() {
            for (int i = 0; i < _overlays.Count; i++) {
                try { _overlays[i](); } catch (Exception __ex) { Guard.Log("绘制功能覆盖层", __ex); }
            }
        }

        private static readonly List<Action<Camera>> _camViews = new List<Action<Camera>>();
        public static void RegisterCameraView(Action<Camera> apply) {
            if (apply != null && !_camViews.Contains(apply)) _camViews.Add(apply);
            FeatureRuntime.RecordRevoke(() => _camViews.Remove(apply));
            FeatureRuntime.RecordSubscription("RegisterCameraView");
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
            FeatureRuntime.RecordRevoke(() => _sceneHooks.Remove(onSceneChanged));
            FeatureRuntime.RecordSubscription("RegisterSceneHook");
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
            FeatureRuntime.RecordRevoke(() => _dismissHooks.Remove(dismiss));
            FeatureRuntime.RecordSubscription("RegisterDismissHook");
        }
        public static void FireDismissUi() {
            for (int i = 0; i < _dismissHooks.Count; i++) {
                try { _dismissHooks[i](); } catch (Exception __ex) { Guard.Log("收起功能 UI", __ex); }
            }
        }

        // 分区：FeatureRuntime（组合根接入：注册包装 / 统一撤销）

        internal static void InstallFeatureRuntime() { FeatureRuntime.SetRevokeSink(RevokeFeatureLease); }

        internal static void FeatureNav(string sec, int rank) { FeatureRuntime.RecordNav(sec); Nav(sec, rank); }
        internal static void FeatureNavHide(string sec) { FeatureRuntime.RecordNav(sec); NavHide(sec); }
        internal static void FeatureTick(string name, Action hook) {
            FeatureRuntime.RecordTick(name);
            RegisterTick(name, hook);
            SetTickOwner(name, FeatureRuntime.CurrentId);
        }
        internal static void FeatureShutdown(string name, Action hook) { FeatureRuntime.RecordShutdown(name); RegisterShutdown(name, hook); }
        internal static void FeatureScene(Action hook) { FeatureRuntime.RecordAnonymous(hook); RegisterSceneHook(hook); }
        internal static void FeatureDismiss(Action hook) { FeatureRuntime.RecordAnonymous(hook); RegisterDismissHook(hook); }
        internal static void FeatureWheel(Func<float, bool> hook) { FeatureRuntime.RecordWheel(hook); RegisterWheelHook(hook); }
        internal static void FeatureHotkey(string id, string label, Action action) { FeatureRuntime.RecordHotkey(id); HotkeyAction(id, label, action); }
        internal static void FeaturePageSimple(string section, PageRenderFn render) {
            FeatureRuntime.RecordPage(section); RegisterPage(section, render);
        }
        internal static void FeaturePageFull(string section, string id, string zh, string en, int order, PageRenderFn render) {
            FeatureRuntime.RecordPage(section + "/" + id);
            RegisterPage(section, id, zh, en, order, render);
        }

        // 按账本撤销运行期资源。注意：页面 / 侧栏 / 快捷键注册在关机时不撤销，进程退出即可。
        internal static void RevokeFeatureLease(FeatureLease lease) {
            if (lease == null) return;
            UnregisterTicksByOwner(lease.Id);
            UnregisterShutdownsByNames(lease.Shutdowns);
            for (int i = 0; i < lease.Anonymous.Count; i++) {
                Action a = lease.Anonymous[i];
                _sceneHooks.Remove(a);
                _dismissHooks.Remove(a);
                _overlays.Remove(a);
            }
            for (int i = 0; i < lease.Wheels.Count; i++) _wheelHooks.Remove(lease.Wheels[i]);
            // 能力容器的撤销统一走 lease.Revokes（见 FeatureRuntime.Dispose）。
        }

        // 滚轮分发：返回 true = 已消费（SR 据此 MarkWheelUsed）。
        private static readonly List<Func<float, bool>> _wheelHooks = new List<Func<float, bool>>();
        public static void RegisterWheelHook(Func<float, bool> onWheel) {
            if (onWheel != null && !_wheelHooks.Contains(onWheel)) _wheelHooks.Add(onWheel);
            FeatureRuntime.RecordRevoke(() => _wheelHooks.Remove(onWheel));
            FeatureRuntime.RecordSubscription("RegisterWheelHook");
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
