import { useState } from 'react';
import { ActivityIndicator, StyleSheet, Text, TextInput, TouchableOpacity, View } from 'react-native';
import { api, setServerUrl } from '../api';
import { normaliseServerUrl, storage } from '../storage';
import { theme } from '../theme';

/**
 * First screen on a fresh install: where is the server?
 *
 * There is no cloud to fall back on. The app has to be told the address of
 * a box on the hospital's own LAN, so this is the one piece of setup that
 * cannot be skipped. Phase 2 prints a config QR at the end of the installer
 * to remove the typing; until then it is entered by hand.
 */
export function ServerSetupScreen({ onDone }: { onDone: (url: string) => void }) {
  const [value, setValue] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function connect() {
    const url = normaliseServerUrl(value);
    if (!url) {
      setError('Enter an address like 192.168.1.50 or hospitalpm.local');
      return;
    }

    setBusy(true);
    setError(null);
    try {
      // Probe before saving. Storing an unreachable address just moves the
      // failure to the login screen, where the cause is less obvious.
      await api.health(url);
      await storage.setServerUrl(url);
      setServerUrl(url);
      onDone(url);
    } catch {
      setError(
        `Could not reach ${url}. Check the device is on the hospital Wi-Fi and the address is right.`,
      );
    } finally {
      setBusy(false);
    }
  }

  return (
    <View style={styles.container}>
      <Text style={styles.title}>Hospital PM</Text>
      <Text style={styles.subtitle}>Connect this device to your hospital&rsquo;s server.</Text>

      <Text style={styles.label}>Server address</Text>
      <TextInput
        style={styles.input}
        value={value}
        onChangeText={setValue}
        placeholder="192.168.1.50 or hospitalpm.local"
        placeholderTextColor={theme.muted}
        autoCapitalize="none"
        autoCorrect={false}
        keyboardType="url"
        editable={!busy}
      />

      <Text style={styles.hint}>
        Ask your biomedical department for this. It is shown on the server&rsquo;s dashboard.
      </Text>

      {error ? <Text style={styles.error}>{error}</Text> : null}

      <TouchableOpacity
        style={[styles.button, busy && styles.buttonDisabled]}
        onPress={() => void connect()}
        disabled={busy}
      >
        {busy ? <ActivityIndicator color="#fff" /> : <Text style={styles.buttonText}>Connect</Text>}
      </TouchableOpacity>
    </View>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, justifyContent: 'center', padding: 24, backgroundColor: theme.bg },
  title: { fontSize: 26, fontWeight: '700', color: theme.text },
  subtitle: { fontSize: 15, color: theme.muted, marginTop: 4, marginBottom: 28 },
  label: { fontSize: 13, color: theme.muted, marginBottom: 6 },
  input: {
    borderWidth: 1,
    borderColor: theme.border,
    borderRadius: 8,
    paddingHorizontal: 14,
    paddingVertical: 12,
    fontSize: 16,
    color: theme.text,
    backgroundColor: theme.surface,
  },
  hint: { fontSize: 12, color: theme.muted, marginTop: 8 },
  error: { color: theme.danger, marginTop: 14, fontSize: 14 },
  button: {
    backgroundColor: theme.accent,
    borderRadius: 8,
    paddingVertical: 15,
    alignItems: 'center',
    marginTop: 24,
  },
  buttonDisabled: { opacity: 0.6 },
  buttonText: { color: '#fff', fontSize: 16, fontWeight: '600' },
});
