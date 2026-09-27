# Models

The machine learning models some tools use (speech to text first), each fetched from its project's own release and checked against a pinned SHA-256. They live in the models folder, not the cache, so clearing the cache keeps them.

- `model_list` names every model, what it is for, its size and whether it is downloaded.
- `model_download` fetches one. They are large (the speech model is 1.6 GB): ask the person before you fetch one. No other tool downloads anything; a tool that needs a missing model refuses with `model-missing` and says which.
