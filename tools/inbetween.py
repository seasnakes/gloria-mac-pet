"""Optical-flow in-betweens for sprite cells (192x208 RGBA, premultiplied blend).

morph(a, b, t) -> RGBA cell at time t between a and b, using DIS flow both ways
(Super SloMo style flow approximation at t, then a visibility-weighted blend).
"""
import cv2
import numpy as np

UP = 3  # compute flow on an upscaled copy for sub-pixel precision


def _prem(c):
    f = c.astype(np.float32) / 255.0
    f[..., :3] *= f[..., 3:4]
    return f


def _unprem(f):
    a = np.clip(f[..., 3:4], 0, 1)
    rgb = np.where(a > 1e-4, f[..., :3] / np.maximum(a, 1e-4), 0)
    return (np.clip(np.concatenate([rgb, a], -1), 0, 1) * 255 + 0.5).astype(np.uint8)


def _gray(p):
    # premultiplied colour over mid grey, plus alpha as extra contrast
    rgb = p[..., :3] + (1 - p[..., 3:4]) * 0.5
    g = cv2.cvtColor((rgb * 255).astype(np.uint8), cv2.COLOR_RGB2GRAY)
    return g


def flows(a, b):
    pa, pb = _prem(a), _prem(b)
    ga = cv2.resize(_gray(pa), None, fx=UP, fy=UP, interpolation=cv2.INTER_CUBIC)
    gb = cv2.resize(_gray(pb), None, fx=UP, fy=UP, interpolation=cv2.INTER_CUBIC)
    dis = cv2.DISOpticalFlow_create(cv2.DISOPTICAL_FLOW_PRESET_ULTRAFAST)
    dis.setFinestScale(0)
    dis.setPatchSize(12)
    dis.setPatchStride(3)
    dis.setGradientDescentIterations(40)
    dis.setVariationalRefinementIterations(10)
    dis.setVariationalRefinementAlpha(30)
    f01 = dis.calc(ga, gb, None)
    f10 = dis.calc(gb, ga, None)
    sm = lambda f: cv2.resize(f, (a.shape[1], a.shape[0]), interpolation=cv2.INTER_AREA) / UP
    return sm(f01), sm(f10)


def _warp(p, f):
    h, w = p.shape[:2]
    gx, gy = np.meshgrid(np.arange(w, dtype=np.float32), np.arange(h, dtype=np.float32))
    return cv2.remap(p, gx + f[..., 0], gy + f[..., 1], cv2.INTER_LINEAR, borderMode=cv2.BORDER_CONSTANT, borderValue=0)


def morph(a, b, t, fl=None):
    pa, pb = _prem(a), _prem(b)
    f01, f10 = fl if fl is not None else flows(a, b)
    ft0 = -(1 - t) * t * f01 + t * t * f10
    ft1 = (1 - t) ** 2 * f01 - t * (1 - t) * f10
    wa = _warp(pa, ft0)
    wb = _warp(pb, ft1)
    out = (1 - t) * wa + t * wb
    return _unprem(out)


def warp_only(a, b, t, fl=None):
    """In-between without cross-fading: move the nearer key frame along the flow (no ghosting)."""
    pa, pb = _prem(a), _prem(b)
    f01, f10 = fl if fl is not None else flows(a, b)
    if t <= 0.5:
        ft0 = -(1 - t) * t * f01 + t * t * f10
        return _unprem(_warp(pa, ft0))
    ft1 = (1 - t) ** 2 * f01 - t * (1 - t) * f10
    return _unprem(_warp(pb, ft1))
