#!/usr/bin/env python3
# Fake input through XTEST, for the Mint box (no xdotool): python3 xinput.py click <screen x> <screen y> | key <keysym> | chord <modifier keysym> <keysym> (e.g. chord Control_L q)
# | type <text> (a path or name, on a US layout: letters, digits, space and / . - _). Needs DISPLAY and XAUTHORITY as in "Running the app".
import ctypes, sys, time
x11 = ctypes.CDLL("libX11.so.6"); xt = ctypes.CDLL("libXtst.so.6")
x11.XOpenDisplay.restype = ctypes.c_void_p
x11.XKeysymToKeycode.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
x11.XStringToKeysym.argtypes = [ctypes.c_char_p]; x11.XStringToKeysym.restype = ctypes.c_ulong
xt.XTestFakeMotionEvent.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_ulong]
xt.XTestFakeButtonEvent.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.c_int, ctypes.c_ulong]
xt.XTestFakeKeyEvent.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.c_int, ctypes.c_ulong]
d = x11.XOpenDisplay(None)
def click(x, y):
    xt.XTestFakeMotionEvent(d, -1, x, y, 0); x11.XFlush(d); time.sleep(.15)
    xt.XTestFakeButtonEvent(d, 1, 1, 0); xt.XTestFakeButtonEvent(d, 1, 0, 0); x11.XFlush(d)
def key(name):
    kc = x11.XKeysymToKeycode(d, x11.XStringToKeysym(name.encode()))
    xt.XTestFakeKeyEvent(d, kc, 1, 0); xt.XTestFakeKeyEvent(d, kc, 0, 0); x11.XFlush(d)
def press(keysym, shift):
    kc = x11.XKeysymToKeycode(d, x11.XStringToKeysym(keysym.encode()))
    sh = x11.XKeysymToKeycode(d, x11.XStringToKeysym(b"Shift_L"))
    if shift: xt.XTestFakeKeyEvent(d, sh, 1, 0)
    xt.XTestFakeKeyEvent(d, kc, 1, 0); xt.XTestFakeKeyEvent(d, kc, 0, 0)
    if shift: xt.XTestFakeKeyEvent(d, sh, 0, 0)
    x11.XFlush(d); time.sleep(.03)
def type_text(text):
    names = {"/": "slash", ".": "period", "-": "minus", " ": "space", "_": "minus"}
    for ch in text:
        press(names.get(ch, ch.lower()), ch.isupper() or ch == "_")
cmd = sys.argv[1]
if cmd == "type": type_text(sys.argv[2])
if cmd == "click": click(int(sys.argv[2]), int(sys.argv[3]))
elif cmd == "key": key(sys.argv[2])
if cmd == "chord":
    mod = x11.XKeysymToKeycode(d, x11.XStringToKeysym(sys.argv[2].encode()))
    k = x11.XKeysymToKeycode(d, x11.XStringToKeysym(sys.argv[3].encode()))
    xt.XTestFakeKeyEvent(d, mod, 1, 0); xt.XTestFakeKeyEvent(d, k, 1, 0); xt.XTestFakeKeyEvent(d, k, 0, 0); xt.XTestFakeKeyEvent(d, mod, 0, 0); x11.XFlush(d)
