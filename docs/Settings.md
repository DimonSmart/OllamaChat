# Settings Feature Documentation

This document explains the Settings functionality that has been added to the OllamaChat application.

## Overview

The Settings page allows users to customize their chat experience with the following options:
- **Default Model**: Select which model to use by default when starting a new chat
- **Default Chat Message**: Pre-fill the chat input with a default message (useful for testing)

## How It Works

### User Settings Storage

User settings are stored in a JSON file located at:
- API: `[App Directory]/UserData/user_settings.json`

The settings are not committed to the repository, allowing each user to have their own configuration.

### Available Settings

Currently, the following settings are available:

1. **Default Model**
   - Automatically selects your preferred Ollama model when starting a new chat
   - The model must be installed and available in Ollama

2. **Default Chat Message**
   - Pre-fills the chat input with your specified text
   - Useful for repeatedly testing the same prompt

## Technical Implementation

The settings functionality is implemented with:

- An API endpoint for saving and loading settings
- A client-side service that caches settings for better performance
- A settings page in the UI for configuration
- Integration with the chat page to apply the settings

## Future Enhancements

Potential future additions to the settings page:

- Theme preferences (light/dark mode default)
- Chat history preferences
- API connection settings
- Function visibility options
