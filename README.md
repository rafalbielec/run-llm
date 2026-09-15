# Run LLM models locally with .NET 10

I wanted to see how hard it would be to run a local LLM with .NET. Turns out not that hard. This is a console app that downloads the model configured in llm.json, loads it into memory, then you chat with it straight in the terminal. Replies come back as Markdown, with code blocks properly syntax-highlighted.

I used a GGUF model &ndash; MLX is still an issue with .NET &ndash; running on my Mac. Should work cross-platform since it’s just LLamaSharp and GGUF. Change the URL in llm.json to try a different model.

I used Spectre.Console to render the effects.

[Tiel-Coder on HuggingFace](https://huggingface.co/peculiar-ragdoll/Tiel-Coder-35B-A3B-GGUF)

## Screenshots from the app

![Downloading model](download.jpg)

![Basic prompt with C# code](output.jpg)
