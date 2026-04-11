window.voiceInputInterop = (() => {
    const states = new Map();

    const getInput = (inputId) => document.getElementById(inputId);

    const ensureState = (inputId) => {
        let state = states.get(inputId);
        if (!state) {
            state = {
                input: null,
                selectionStart: 0,
                selectionEnd: 0,
                stream: null,
                recorder: null,
                stopPromise: null,
                eventHandlers: null
            };
            states.set(inputId, state);
        }

        return state;
    };

    const captureSelection = (state) => {
        if (!state.input) {
            return;
        }

        const fallbackPosition = state.input.value?.length ?? 0;
        state.selectionStart = typeof state.input.selectionStart === "number"
            ? state.input.selectionStart
            : fallbackPosition;
        state.selectionEnd = typeof state.input.selectionEnd === "number"
            ? state.input.selectionEnd
            : state.selectionStart;
    };

    const releaseMediaResources = (state) => {
        if (state.stream) {
            for (const track of state.stream.getTracks()) {
                track.stop();
            }

            state.stream = null;
        }

        state.recorder = null;
        state.stopPromise = null;
    };

    const detachInputHandlers = (state) => {
        if (!state.input || !state.eventHandlers) {
            return;
        }

        for (const [eventName, handler] of state.eventHandlers) {
            state.input.removeEventListener(eventName, handler);
        }

        state.eventHandlers = null;
    };

    const attachInputHandlers = (inputId, input, state) => {
        if (state.input === input && state.eventHandlers) {
            return;
        }

        detachInputHandlers(state);
        state.input = input;

        const syncSelection = () => captureSelection(state);
        state.eventHandlers = [
            ["click", syncSelection],
            ["focus", syncSelection],
            ["input", syncSelection],
            ["keyup", syncSelection],
            ["select", syncSelection]
        ];

        for (const [eventName, handler] of state.eventHandlers) {
            input.addEventListener(eventName, handler);
        }

        captureSelection(state);
        states.set(inputId, state);
    };

    const ensureInput = (inputId) => {
        const input = getInput(inputId);
        if (!input) {
            throw new Error("Message input is unavailable.");
        }

        const state = ensureState(inputId);
        attachInputHandlers(inputId, input, state);
        return state;
    };

    const pickMimeType = () => {
        const candidates = [
            "audio/webm;codecs=opus",
            "audio/webm",
            "audio/ogg;codecs=opus",
            "audio/mp4"
        ];

        return candidates.find((candidate) => MediaRecorder.isTypeSupported(candidate)) ?? "";
    };

    const toFriendlyError = (error, fallbackMessage) => {
        if (!error) {
            return fallbackMessage;
        }

        if (typeof error === "string") {
            return error;
        }

        switch (error.name) {
            case "NotAllowedError":
                return "Microphone access was denied.";
            case "NotFoundError":
                return "No microphone was found.";
            case "NotReadableError":
                return "Microphone is already in use.";
            case "SecurityError":
                return "Microphone access requires a secure context.";
            default:
                return error.message || fallbackMessage;
        }
    };

    const decodeAudioBlob = async (blob) => {
        const AudioContextType = window.AudioContext || window.webkitAudioContext;
        if (!AudioContextType) {
            throw new Error("Audio decoding is not supported in this browser.");
        }

        const audioContext = new AudioContextType();
        try {
            const arrayBuffer = await blob.arrayBuffer();
            return await audioContext.decodeAudioData(arrayBuffer.slice(0));
        } finally {
            await audioContext.close();
        }
    };

    const downmixToMono = (audioBuffer) => {
        const monoChannel = new Float32Array(audioBuffer.length);

        for (let channelIndex = 0; channelIndex < audioBuffer.numberOfChannels; channelIndex++) {
            const channelData = audioBuffer.getChannelData(channelIndex);
            for (let sampleIndex = 0; sampleIndex < audioBuffer.length; sampleIndex++) {
                monoChannel[sampleIndex] += channelData[sampleIndex] / audioBuffer.numberOfChannels;
            }
        }

        return monoChannel;
    };

    const whisperSampleRate = 16000;

    const resampleToSampleRate = (samples, sourceSampleRate, targetSampleRate) => {
        if (sourceSampleRate === targetSampleRate) {
            return samples;
        }

        if (!Number.isFinite(sourceSampleRate) || sourceSampleRate <= 0) {
            throw new Error("The recorded audio sample rate is invalid.");
        }

        const sampleRateRatio = sourceSampleRate / targetSampleRate;
        const targetLength = Math.max(1, Math.round(samples.length / sampleRateRatio));
        const resampled = new Float32Array(targetLength);

        for (let targetIndex = 0; targetIndex < targetLength; targetIndex++) {
            const sourceIndex = targetIndex * sampleRateRatio;
            const sourceFloor = Math.floor(sourceIndex);
            const sourceCeiling = Math.min(sourceFloor + 1, samples.length - 1);
            const interpolation = sourceIndex - sourceFloor;
            const sourceValue = samples[Math.min(sourceFloor, samples.length - 1)] ?? 0;
            const nextValue = samples[sourceCeiling] ?? sourceValue;

            resampled[targetIndex] = sourceValue + ((nextValue - sourceValue) * interpolation);
        }

        return resampled;
    };

    const encodeWave = (samples, sampleRate) => {
        const bytesPerSample = 2;
        const dataSize = samples.length * bytesPerSample;
        const buffer = new ArrayBuffer(44 + dataSize);
        const view = new DataView(buffer);

        const writeString = (offset, value) => {
            for (let index = 0; index < value.length; index++) {
                view.setUint8(offset + index, value.charCodeAt(index));
            }
        };

        writeString(0, "RIFF");
        view.setUint32(4, 36 + dataSize, true);
        writeString(8, "WAVE");
        writeString(12, "fmt ");
        view.setUint32(16, 16, true);
        view.setUint16(20, 1, true);
        view.setUint16(22, 1, true);
        view.setUint32(24, sampleRate, true);
        view.setUint32(28, sampleRate * bytesPerSample, true);
        view.setUint16(32, bytesPerSample, true);
        view.setUint16(34, 16, true);
        writeString(36, "data");
        view.setUint32(40, dataSize, true);

        let offset = 44;
        for (const sample of samples) {
            const normalized = Math.max(-1, Math.min(1, sample));
            view.setInt16(offset, normalized < 0 ? normalized * 0x8000 : normalized * 0x7fff, true);
            offset += bytesPerSample;
        }

        return new Blob([buffer], { type: "audio/wav" });
    };

    const convertRecordingToWave = async (blob) => {
        const audioBuffer = await decodeAudioBlob(blob);
        const monoSamples = downmixToMono(audioBuffer);
        const resampledSamples = resampleToSampleRate(monoSamples, audioBuffer.sampleRate, whisperSampleRate);
        return encodeWave(resampledSamples, whisperSampleRate);
    };

    return {
        registerInput(inputId) {
            ensureInput(inputId);
        },

        async startRecording(inputId) {
            if (!window.isSecureContext) {
                throw new Error("Microphone access requires HTTPS or localhost.");
            }

            if (!navigator.mediaDevices?.getUserMedia) {
                throw new Error("Microphone access is not supported in this browser.");
            }

            if (typeof MediaRecorder === "undefined") {
                throw new Error("MediaRecorder is not available in this browser.");
            }

            const state = ensureInput(inputId);
            captureSelection(state);

            if (state.recorder?.state === "recording") {
                return;
            }

            try {
                const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
                const chunks = [];
                const mimeType = pickMimeType();
                const recorder = mimeType
                    ? new MediaRecorder(stream, { mimeType })
                    : new MediaRecorder(stream);

                state.stream = stream;
                state.recorder = recorder;
                state.stopPromise = new Promise((resolve, reject) => {
                    recorder.addEventListener("dataavailable", (event) => {
                        if (event.data && event.data.size > 0) {
                            chunks.push(event.data);
                        }
                    });

                    recorder.addEventListener("stop", () => {
                        resolve(new Blob(chunks, { type: recorder.mimeType || mimeType || "audio/webm" }));
                    });

                    recorder.addEventListener("error", () => {
                        reject(new Error("Audio recording failed."));
                    });
                });

                recorder.start();
            } catch (error) {
                releaseMediaResources(state);
                throw new Error(toFriendlyError(error, "Failed to access the microphone."));
            }
        },

        async stopRecording(inputId, endpointUrl) {
            const state = ensureState(inputId);
            if (!state.recorder || !state.stopPromise) {
                throw new Error("Voice recording is not active.");
            }

            if (state.recorder.state !== "inactive") {
                state.recorder.stop();
            }

            try {
                const recordedBlob = await state.stopPromise;
                const waveBlob = await convertRecordingToWave(recordedBlob);
                const formData = new FormData();
                formData.append("audio", waveBlob, "voice-input.wav");

                const response = await fetch(endpointUrl, {
                    method: "POST",
                    body: formData
                });

                const payload = await response.json().catch(() => null);
                if (!response.ok) {
                    throw new Error(payload?.message || "Voice recognition failed.");
                }

                return payload?.text || "";
            } finally {
                releaseMediaResources(state);
            }
        },

        insertTextAtSavedSelection(inputId, insertedText) {
            const state = ensureInput(inputId);
            const input = state.input;
            const currentValue = input?.value || "";
            const start = Math.max(0, Math.min(state.selectionStart, currentValue.length));
            const end = Math.max(start, Math.min(state.selectionEnd, currentValue.length));
            const nextValue = currentValue.slice(0, start) + insertedText + currentValue.slice(end);
            const caretPosition = start + insertedText.length;

            input.value = nextValue;
            input.dispatchEvent(new Event("input", { bubbles: true }));
            input.focus();
            input.setSelectionRange?.(caretPosition, caretPosition);

            state.selectionStart = caretPosition;
            state.selectionEnd = caretPosition;

            return {
                value: nextValue
            };
        },

        dispose(inputId) {
            const state = states.get(inputId);
            if (!state) {
                return;
            }

            if (state.recorder?.state === "recording") {
                state.recorder.stop();
            }

            releaseMediaResources(state);
            detachInputHandlers(state);
            states.delete(inputId);
        }
    };
})();
