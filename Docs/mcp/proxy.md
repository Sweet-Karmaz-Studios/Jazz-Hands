# Proxies

A proxy is a small, easy-to-decode copy of heavy footage (4K, AV1, 10-bit HEVC) that the preview plays instead, so editing stays smooth. Exports always read the original.

`proxy_list` says which movies have one and which would benefit; `proxy_generate` makes them (one, all, or `auto` for the heavy ones); `proxy_set_enabled` switches the preview between proxies and originals; `proxy_remove` deletes them. Proxies are files in the cache, not part of the project.
