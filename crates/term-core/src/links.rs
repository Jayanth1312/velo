//! Heuristic link detection over a rendered row's text. No regex dep: find
//! the whitespace/bracket-delimited token under the column, accept it if it
//! matches a known scheme/path shape, trim trailing punctuation.

pub struct Link {
    pub col_start: u16,
    pub col_end: u16,
    pub target: String,
}

/// Chars that end a token even without whitespace.
fn is_delim(c: char) -> bool {
    c.is_whitespace() || matches!(c, '(' | ')' | '[' | ']' | '{' | '}' | '<' | '>' | '"' | '\'' | '`')
}

pub fn link_in_row(text: &str, col: u16) -> Option<Link> {
    let chars: Vec<char> = text.chars().collect();
    let col = col as usize;
    if col >= chars.len() || is_delim(chars[col]) {
        return None;
    }
    let mut start = col;
    while start > 0 && !is_delim(chars[start - 1]) {
        start -= 1;
    }
    let mut end = col;
    while end + 1 < chars.len() && !is_delim(chars[end + 1]) {
        end += 1;
    }
    // Trim trailing punctuation that's prose, not target.
    while end > start && matches!(chars[end], '.' | ',' | ';' | ':' | '!' | '?') {
        end -= 1;
    }
    let token: String = chars[start..=end].iter().collect();

    let target = if token.starts_with("http://")
        || token.starts_with("https://")
        || token.starts_with("file://")
    {
        token.clone()
    } else if token.starts_with("www.") && token.len() > 4 {
        format!("https://{token}")
    } else if is_pathlike(&token) {
        token.clone()
    } else {
        return None;
    };
    Some(Link { col_start: start as u16, col_end: end as u16, target })
}

/// Absolute-looking paths only: `/…`, `~/…`, `\\…`, `X:\…`, `X:/…`.
/// Relative paths skipped on purpose — too many false positives (and/or).
fn is_pathlike(t: &str) -> bool {
    let b = t.as_bytes();
    t.starts_with('/') && t.len() > 1
        || t.starts_with("~/")
        || t.starts_with(r"\\")
        || (b.len() > 3 && b[0].is_ascii_alphabetic() && b[1] == b':' && (b[2] == b'\\' || b[2] == b'/'))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn hit(text: &str, col: u16) -> Option<String> {
        link_in_row(text, col).map(|l| l.target)
    }

    #[test]
    fn url_mid_text() {
        assert_eq!(
            hit("see https://example.com/a?b=1 now", 10),
            Some("https://example.com/a?b=1".into())
        );
    }

    #[test]
    fn trailing_punctuation_trimmed() {
        assert_eq!(hit("(https://example.com).", 5), Some("https://example.com".into()));
    }

    #[test]
    fn www_gets_scheme() {
        assert_eq!(hit("www.example.com", 3), Some("https://www.example.com".into()));
    }

    #[test]
    fn windows_path() {
        assert_eq!(hit(r"at C:\velo\src\main.rs here", 8), Some(r"C:\velo\src\main.rs".into()));
    }

    #[test]
    fn unix_and_wsl_path() {
        assert_eq!(hit("/mnt/d/velo/README.md", 4), Some("/mnt/d/velo/README.md".into()));
        assert_eq!(hit("~/projects/x", 2), Some("~/projects/x".into()));
    }

    #[test]
    fn col_outside_token_is_none() {
        assert_eq!(hit("see https://example.com now", 1), None);
    }

    #[test]
    fn plain_word_is_none() {
        assert_eq!(hit("hello world", 2), None);
    }

    #[test]
    fn bare_slash_word_is_none() {
        // "and/or" must not read as a path
        assert_eq!(hit("and/or", 2), None);
    }

    #[test]
    fn span_covers_whole_token() {
        let l = link_in_row("x https://a.io y", 5).unwrap();
        assert_eq!((l.col_start, l.col_end), (2, 13));
    }
}
