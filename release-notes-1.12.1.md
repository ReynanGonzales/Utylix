Fix: PDFs on a server (\server\share) open again from shortcuts and Explorer's Recent list

- **PDFs on a server share open from shortcuts and Recent.** When Utylix was already running, a PDF opened from a shortcut to the server folder or from Explorer's Recent list (a path like `\192.168.1.107\main server\…`) did nothing: the running Utylix refused every `\server\share` path from the second copy and dropped it without a word. Files opened through a drive letter such as K: were not affected. They now open, also when a shortcut file (.lnk) or a `file:` address is handed over, and a server that answers late is asked a few times. This applies only to requests from programs on this PC, never to web pages.
- If a file still can't be opened, the window now says why.
