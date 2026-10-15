# SPDX-License-Identifier: GPL-3.0-or-later
"""Receive resolved Unity weights, applying bpy changes only on Blender's main thread."""
import json
import math
import socket
import time
import bpy
from bpy.app.handlers import persistent
from . import osc


class Receiver:
    def __init__(self):
        self.socket = None
        self.scene = None
        self.saved = {}
        self.object_saved = {}
        self.latest = {}
        self.order = {}
        self.session = None
        self.frames = 0
        self.dropped = 0
        self.unmatched = set()
        self.last_packet = 0.0
        self.paused = False
        self.pending = {}
        self.event_timer = None
        self.window_manager = None
        self.generation = 0
        self.last_status = 0.0
        self.metric_start = time.monotonic()
        self.metric_ticks = 0
        self.metric_frames = 0
        self.last_sequence = -1
        self.source_rate = 0.0
        self.update_rate = 0.0
        self.processing_ms = 0.0
        self.last_tick = 0.0
        self.lookup = None
        # bpy timers identify callbacks by object identity. Rebinding self.tick at
        # each access produces a different Python method object.
        self.timer_callback = self.tick

    @property
    def running(self):
        return self.socket is not None

    def start(self, scene, port):
        self.stop()
        next_socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            next_socket.bind(('127.0.0.1', port))
            next_socket.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 1024 * 1024)
            next_socket.setblocking(False)
        except Exception:
            next_socket.close()
            raise
        self.socket = next_socket
        self.scene = scene
        self.frames = self.dropped = 0
        self.metric_start = time.monotonic()
        self.metric_ticks = 0
        self.metric_frames = 0
        self.last_sequence = -1
        self.source_rate = self.update_rate = 0.0
        self.last_status = 0.0
        self.unmatched.clear()
        scene.nyades_ft_toolkit.live_status = f'Listening on 127.0.0.1:{port}; waiting for Unity'
        bpy.app.timers.register(self.timer_callback, first_interval=0.0)

    def enable_window_timer(self, context):
        # Window events keep the viewport responsive even when Blender is idle.
        self.window_manager = context.window_manager
        self.event_timer = self.window_manager.event_timer_add(
            1 / self.scene.nyades_ft_toolkit.live_fps, window=context.window)
        if bpy.app.timers.is_registered(self.timer_callback):
            bpy.app.timers.unregister(self.timer_callback)

    def restore(self):
        for key, value, minimum, maximum, mute in self.saved.values():
            try:
                key.value = value
                key.slider_min = minimum
                key.slider_max = maximum
                key.mute = mute
            except ReferenceError:
                pass
        for obj, show_only in self.object_saved.values():
            try:
                obj.show_only_shape_key = show_only
            except ReferenceError:
                pass

    def stop(self):
        self.generation += 1
        if self.event_timer is not None:
            try:
                self.window_manager.event_timer_remove(self.event_timer)
            except (ReferenceError, RuntimeError):
                pass
            self.event_timer = self.window_manager = None
        if self.socket:
            self.socket.close()
            self.socket = None
        if bpy.app.timers.is_registered(self.timer_callback):
            bpy.app.timers.unregister(self.timer_callback)
        self.restore()
        if self.scene:
            try:
                self.scene.nyades_ft_toolkit.live_status = 'Stopped; original sliders restored'
            except (AttributeError, ReferenceError):
                pass
        self.saved.clear()
        self.object_saved.clear()
        self.latest.clear()
        self.pending.clear()
        self.order.clear()
        self.session = None
        self.scene = None
        self.paused = False

    def resolve(self, frame):
        # Round Trip IDs avoid duplicate names. Fallback accepts only a unique mesh name.
        token = frame.get('id', '')
        cache_key = (token, frame.get('name', ''))
        if self.lookup is not None and cache_key in self.lookup:
            return self.lookup[cache_key]
        obj = self.scene.objects.get(token) if token else None
        if not obj or obj.type != 'MESH':
            name = frame.get('name', '')
            candidates = [o for o in self.scene.objects if o.type == 'MESH' and (o.name == name or o.get('unity_name') == name)]
            obj = candidates[0] if len(candidates) == 1 else None
        if self.lookup is not None:
            self.lookup[cache_key] = obj
        return obj

    def apply(self, frame, now=None, defer=False):
        if not isinstance(frame, dict) or frame.get('version') != 1:
            raise ValueError('Unsupported FT preview frame')
        session = frame.get('session')
        sequence = frame.get('sequence')
        shapes = frame.get('shapes')
        if not isinstance(session, str) or not isinstance(sequence, int) or sequence < 0 or not isinstance(shapes, list) or len(shapes) > 2048:
            raise ValueError('Invalid FT preview frame')
        if session != self.session:
            self.restore()
            self.order.clear()
            self.latest.clear()
            self.pending.clear()
            self.session = session
            self.last_sequence = -1
        obj = self.resolve(frame)
        if obj is None or obj.data.shape_keys is None:
            self.unmatched.add(frame.get('id') or frame.get('name') or '(unnamed mesh)')
            return
        # Editing coordinates and editing live values must not fight over Blender data.
        if obj.mode != 'OBJECT':
            return
        keys = obj.data.shape_keys
        if not keys.use_relative:
            self.unmatched.add(obj.name + ': absolute keys')
            return
        timestamp = now if now is not None else time.monotonic()
        for shape in shapes:
            if not isinstance(shape, dict) or not isinstance(shape.get('name'), str):
                raise ValueError('Invalid FT shape')
            value = shape.get('value')
            if not isinstance(value, (int, float)) or not math.isfinite(value):
                raise ValueError('Invalid FT weight')
            key = keys.key_blocks.get(shape['name'])
            if key is None:
                self.unmatched.add(obj.name + '/' + shape['name'])
                continue
            if key == keys.reference_key:
                continue
            token = key.as_pointer()
            if sequence < self.order.get(token, -1):
                continue
            self.order[token] = sequence
            if token not in self.saved:
                self.saved[token] = (key, key.value, key.slider_min, key.slider_max, key.mute)
            if obj.as_pointer() not in self.object_saved:
                self.object_saved[obj.as_pointer()] = (obj, obj.show_only_shape_key)
            value = max(-10.0, min(10.0, float(value)))
            self.latest[token] = (key, value, timestamp, obj)
            self.pending[token] = (key, value, obj)
        if not defer:
            self.flush()
        self.last_packet = timestamp
        self.frames += 1
        if sequence > self.last_sequence:
            self.metric_frames += 1
            self.last_sequence = sequence

    @staticmethod
    def _set(key, value, obj):
        changed = False
        if key.slider_min > value:
            key.slider_min = value
            changed = True
        if key.slider_max < value:
            key.slider_max = value
            changed = True
        if key.mute:
            key.mute = False
            changed = True
        if abs(key.value - value) > 1e-6:
            key.value = value  # RNA tags the dependency graph; no mesh.update needed.
            changed = True
        if obj.show_only_shape_key:
            obj.show_only_shape_key = False
            changed = True
        return changed

    def flush(self):
        changed = False
        for key, value, obj in self.pending.values():
            try:
                if obj.mode == 'OBJECT':
                    changed = self._set(key, value, obj) or changed
            except ReferenceError:
                pass
        self.pending.clear()
        return changed

    def tick(self):
        if not self.socket:
            return None
        began = time.monotonic()
        self.last_tick = began
        interval = 1 / self.scene.nyades_ft_toolkit.live_fps
        if self.paused:
            return interval
        try:
            self.lookup = {}
            for _ in range(2048):
                try:
                    data, _ = self.socket.recvfrom(65507)
                except BlockingIOError:
                    break
                try:
                    for address, args in osc.decode(data):
                        if address == '/nyades/ft/frame' and len(args) == 1 and isinstance(args[0], str):
                            self.apply(json.loads(args[0]), defer=True)
                except (ValueError, TypeError, KeyError, ReferenceError, json.JSONDecodeError):
                    self.dropped += 1
            self.lookup = None
            changed = self.flush()
            now = time.monotonic()
            for token, (key, _, received, obj) in list(self.latest.items()):
                if now - received > 1.0:
                    saved = self.saved.get(token)
                    if obj.mode != 'OBJECT':
                        continue
                    if saved:
                        _, key.value, key.slider_min, key.slider_max, key.mute = saved
                        changed = True
                    del self.latest[token]
            self.metric_ticks += 1
            elapsed = now - self.metric_start
            if elapsed >= 1:
                self.update_rate = self.metric_ticks / elapsed
                self.source_rate = self.metric_frames / elapsed
                self.metric_ticks = 0
                self.metric_frames = 0
                self.metric_start = now
            if now - self.last_status >= .5:
                state = 'Receiving' if self.last_packet and now - self.last_packet < 1.0 else 'Waiting for Unity'
                self.scene.nyades_ft_toolkit.live_status = f'{state}; Unity: {self.source_rate:.0f} frames/s; receiver: {self.update_rate:.0f} updates/s; processing: {self.processing_ms:.1f} ms; packets: {self.frames}; unmatched: {len(self.unmatched)}; bad packets: {self.dropped}'
                self.last_status = now
                changed = True
            if changed:
                for window in bpy.context.window_manager.windows:
                    for area in window.screen.areas:
                        if area.type == 'VIEW_3D':
                            area.tag_redraw()
        except Exception as error:
            scene = self.scene
            self.stop()
            self.lookup = None
            if scene:
                scene.nyades_ft_toolkit.live_status = 'Receiver stopped: ' + str(error)
        self.processing_ms = (time.monotonic() - began) * 1000
        return max(.001, interval - self.processing_ms / 1000) if self.socket else None


receiver = Receiver()


@persistent
def before_load(_):
    receiver.stop()


@persistent
def before_undo(_):
    receiver.stop()


@persistent
def before_save(_):
    if receiver.running:
        receiver.paused = True
        receiver.restore()


@persistent
def after_save(_):
    if receiver.running:
        receiver.paused = False
        for key, value, _, obj in receiver.latest.values():
            try:
                if obj.mode == 'OBJECT':
                    receiver._set(key, value, obj)
            except ReferenceError:
                pass


HANDLERS = ((bpy.app.handlers.load_pre, before_load), (bpy.app.handlers.undo_pre, before_undo),
            (bpy.app.handlers.save_pre, before_save), (bpy.app.handlers.save_post, after_save))


def register():
    for handlers, callback in HANDLERS:
        if callback not in handlers:
            handlers.append(callback)


def unregister():
    receiver.stop()
    for handlers, callback in HANDLERS:
        if callback in handlers:
            handlers.remove(callback)
