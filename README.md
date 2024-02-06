# coder
AI coding assistant for C#.

## Usage

- Create "openai.key" file containing OpenAI API key, in current folder or in /usr/bin.
- Add "// TODO: <description>" comments in methods.
- Run "coder <project_path>".
- New code is processed and changes merged into the *.cs files.
- In case of conflicts or errors check <project_path>/_prompts folder.
- Previous *.cs files (before merging) are renamed to *.old.
- For options to run the process in stages see Program.cs.
