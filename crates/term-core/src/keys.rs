//! Key -> escape-sequence encoding for keys that emit no `WM_CHAR` (arrows,
//! F-keys, Home/End/PgUp/PgDn/Ins/Del). Pure and platform-independent so it's
//! unit-testable without a Windows target; callers pass raw Win32 virtual-key
//! codes (stable across platforms, so no `windows` crate dependency here).

const VK_END: u16 = 0x23;
const VK_HOME: u16 = 0x24;
const VK_LEFT: u16 = 0x25;
const VK_UP: u16 = 0x26;
const VK_RIGHT: u16 = 0x27;
const VK_DOWN: u16 = 0x28;
const VK_PRIOR: u16 = 0x21; // Page Up
const VK_NEXT: u16 = 0x22; // Page Down
const VK_INSERT: u16 = 0x2D;
const VK_DELETE: u16 = 0x2E;
const VK_TAB: u16 = 0x09;
const VK_F1: u16 = 0x70;
const VK_F12: u16 = 0x7B;

/// xterm modifier parameter: 1 + shift(1) + alt(2) + ctrl(4).
fn mod_param(ctrl: bool, shift: bool, alt: bool) -> u8 {
    1 + shift as u8 + (alt as u8) * 2 + (ctrl as u8) * 4
}

/// Encode a navigation/function key into the byte sequence the PTY expects.
/// Returns `None` for keys not handled here (e.g. plain printable keys, which
/// arrive via `WM_CHAR`/`on_char` instead).
pub fn key_seq(vk: u16, ctrl: bool, shift: bool, alt: bool, app_cursor: bool) -> Option<Vec<u8>> {
    let any_mod = ctrl || shift || alt;
    let m = mod_param(ctrl, shift, alt);

    // Shift+Tab -> CBT / back-tab. Plain Tab emits a WM_CHAR (0x09) so only the
    // shifted form needs encoding here; Ctrl+Tab is consumed as a tab-switch
    // chord before this is ever reached.
    if vk == VK_TAB {
        return (shift && !ctrl).then(|| b"\x1b[Z".to_vec());
    }

    // F1-F4: ESC O P..S unmodified, CSI 1;m P..S with modifiers.
    if (VK_F1..=VK_F1 + 3).contains(&vk) {
        let letter = b"PQRS"[(vk - VK_F1) as usize];
        return Some(if any_mod {
            format!("\x1b[1;{m}{}", letter as char).into_bytes()
        } else {
            vec![0x1b, b'O', letter]
        });
    }
    // F5-F12: CSI n~ (n has a gap: 15,17,18,19,20,21,23,24 — no 16 or 22).
    if (VK_F1 + 4..=VK_F12).contains(&vk) {
        const CODES: [u8; 8] = [15, 17, 18, 19, 20, 21, 23, 24];
        let n = CODES[(vk - (VK_F1 + 4)) as usize];
        return Some(if any_mod {
            format!("\x1b[{n};{m}~").into_bytes()
        } else {
            format!("\x1b[{n}~").into_bytes()
        });
    }

    // Arrows/Home/End: ESC O <letter> in app-cursor mode with no mods; CSI
    // <letter> with no mods otherwise; CSI 1;m <letter> with mods.
    let arrow_letter = match vk {
        v if v == VK_UP => Some(b'A'),
        v if v == VK_DOWN => Some(b'B'),
        v if v == VK_RIGHT => Some(b'C'),
        v if v == VK_LEFT => Some(b'D'),
        v if v == VK_HOME => Some(b'H'),
        v if v == VK_END => Some(b'F'),
        _ => None,
    };
    if let Some(letter) = arrow_letter {
        return Some(if any_mod {
            format!("\x1b[1;{m}{}", letter as char).into_bytes()
        } else if app_cursor {
            vec![0x1b, b'O', letter]
        } else {
            vec![0x1b, b'[', letter]
        });
    }

    // PgUp/PgDn/Insert/Delete: CSI n~ unmodified, CSI n;m~ with mods.
    let n = match vk {
        v if v == VK_PRIOR => Some(5),
        v if v == VK_NEXT => Some(6),
        v if v == VK_INSERT => Some(2),
        v if v == VK_DELETE => Some(3),
        _ => None,
    }?;
    Some(if any_mod {
        format!("\x1b[{n};{m}~").into_bytes()
    } else {
        format!("\x1b[{n}~").into_bytes()
    })
}

/// Encode a mouse event as an SGR (1006) mouse-report sequence:
/// `CSI < b ; col ; row M` on press/motion, `CSI < b ; col ; row m` on release.
/// `col`/`row` are 1-based (viewport cell coordinates + 1). `button` follows
/// xterm's SGR encoding: 0=left, 1=middle, 2=right; callers add 32 for a
/// motion event (button held or motion-tracking) and use 64/65 for wheel
/// up/down (always sent with `pressed = true` — wheel events have no release).
pub fn sgr_mouse_seq(button: u8, col: u16, row: u16, pressed: bool) -> Vec<u8> {
    let suffix = if pressed { 'M' } else { 'm' };
    format!("\x1b[<{button};{col};{row}{suffix}").into_bytes()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn f1_unmodified() {
        assert_eq!(key_seq(VK_F1, false, false, false, false), Some(b"\x1bOP".to_vec()));
    }
    #[test]
    fn shift_tab_backtab() {
        // Shift+Tab -> CBT (back-tab).
        assert_eq!(key_seq(VK_TAB, false, true, false, false), Some(b"\x1b[Z".to_vec()));
    }

    #[test]
    fn plain_tab_none() {
        // Plain Tab arrives via WM_CHAR (0x09); key_seq must not double-encode it.
        assert_eq!(key_seq(VK_TAB, false, false, false, false), None);
    }

    #[test]
    fn f4_unmodified() {
        assert_eq!(key_seq(VK_F1 + 3, false, false, false, false), Some(b"\x1bOS".to_vec()));
    }

    #[test]
    fn f1_ctrl() {
        // m = 1 + 0(shift) + 0(alt) + 4(ctrl) = 5
        assert_eq!(
            key_seq(VK_F1, true, false, false, false),
            Some(b"\x1b[1;5P".to_vec())
        );
    }

    #[test]
    fn f5_unmodified() {
        assert_eq!(key_seq(VK_F1 + 4, false, false, false, false), Some(b"\x1b[15~".to_vec()));
    }

    #[test]
    fn f12_unmodified() {
        assert_eq!(key_seq(VK_F12, false, false, false, false), Some(b"\x1b[24~".to_vec()));
    }

    #[test]
    fn f6_shift() {
        // m = 1 + 1(shift) = 2
        assert_eq!(
            key_seq(VK_F1 + 5, false, true, false, false),
            Some(b"\x1b[17;2~".to_vec())
        );
    }

    #[test]
    fn up_no_mods_no_app_cursor() {
        assert_eq!(key_seq(VK_UP, false, false, false, false), Some(b"\x1b[A".to_vec()));
    }

    #[test]
    fn up_no_mods_app_cursor() {
        assert_eq!(key_seq(VK_UP, false, false, false, true), Some(b"\x1bOA".to_vec()));
    }

    #[test]
    fn up_shift_ignores_app_cursor() {
        // m = 1 + 1(shift) = 2
        assert_eq!(
            key_seq(VK_UP, false, true, false, true),
            Some(b"\x1b[1;2A".to_vec())
        );
    }

    #[test]
    fn ctrl_alt_shift_right() {
        // m = 1 + 1(shift) + 2(alt) + 4(ctrl) = 8
        assert_eq!(
            key_seq(VK_RIGHT, true, true, true, false),
            Some(b"\x1b[1;8C".to_vec())
        );
    }

    #[test]
    fn home_end_no_mods() {
        assert_eq!(key_seq(VK_HOME, false, false, false, false), Some(b"\x1b[H".to_vec()));
        assert_eq!(key_seq(VK_END, false, false, false, false), Some(b"\x1b[F".to_vec()));
    }

    #[test]
    fn pgup_pgdn_no_mods() {
        assert_eq!(key_seq(VK_PRIOR, false, false, false, false), Some(b"\x1b[5~".to_vec()));
        assert_eq!(key_seq(VK_NEXT, false, false, false, false), Some(b"\x1b[6~".to_vec()));
    }

    #[test]
    fn insert_delete_ctrl() {
        // m = 1 + 4(ctrl) = 5
        assert_eq!(
            key_seq(VK_INSERT, true, false, false, false),
            Some(b"\x1b[2;5~".to_vec())
        );
        assert_eq!(
            key_seq(VK_DELETE, true, false, false, false),
            Some(b"\x1b[3;5~".to_vec())
        );
    }

    #[test]
    fn unknown_vk_returns_none() {
        assert_eq!(key_seq(0x41, false, false, false, false), None); // 'A' key
    }

    #[test]
    fn mod_param_math() {
        assert_eq!(mod_param(false, false, false), 1);
        assert_eq!(mod_param(true, false, false), 5);
        assert_eq!(mod_param(false, true, false), 2);
        assert_eq!(mod_param(false, false, true), 3);
        assert_eq!(mod_param(true, true, true), 8);
    }

    #[test]
    fn sgr_mouse_left_press() {
        assert_eq!(sgr_mouse_seq(0, 1, 1, true), b"\x1b[<0;1;1M".to_vec());
    }

    #[test]
    fn sgr_mouse_left_release() {
        assert_eq!(sgr_mouse_seq(0, 1, 1, false), b"\x1b[<0;1;1m".to_vec());
    }

    #[test]
    fn sgr_mouse_coords_are_one_based() {
        // Grid cell (col=9, row=23) 0-based -> reported as 10;24.
        assert_eq!(sgr_mouse_seq(0, 10, 24, true), b"\x1b[<0;10;24M".to_vec());
    }

    #[test]
    fn sgr_mouse_motion_adds_32() {
        // Left-button drag: button 0 + 32 motion flag = 32.
        assert_eq!(sgr_mouse_seq(0 + 32, 5, 5, true), b"\x1b[<32;5;5M".to_vec());
    }

    #[test]
    fn sgr_mouse_wheel_up() {
        assert_eq!(sgr_mouse_seq(64, 3, 4, true), b"\x1b[<64;3;4M".to_vec());
    }

    #[test]
    fn sgr_mouse_wheel_down() {
        assert_eq!(sgr_mouse_seq(65, 3, 4, true), b"\x1b[<65;3;4M".to_vec());
    }

    #[test]
    fn ctrl_delete_kills_word_sequence() {
        assert_eq!(key_seq(VK_DELETE, true, false, false, false).unwrap(), b"\x1b[3;5~");
    }

    #[test]
    fn ctrl_home_end() {
        assert_eq!(key_seq(VK_HOME, true, false, false, false).unwrap(), b"\x1b[1;5H");
        assert_eq!(key_seq(VK_END, true, false, false, false).unwrap(), b"\x1b[1;5F");
    }

    #[test]
    fn alt_arrows() {
        assert_eq!(key_seq(VK_LEFT, false, false, true, false).unwrap(), b"\x1b[1;3D");
        assert_eq!(key_seq(VK_RIGHT, false, false, true, false).unwrap(), b"\x1b[1;3C");
    }
}
