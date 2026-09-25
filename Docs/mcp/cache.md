# The cache

The editor keeps thumbnails, waveforms, media probes, keyframe indexes and proxies in a cache folder with a size limit (20 GB by default), clearing the least recently used first. None of it is part of the project; anything cleared is made again when needed.

`cache_stats` shows what it holds; `cache_clear` empties it or parts of it; `cache_configure` moves it or changes the limit. There is rarely a reason to touch it while editing.
