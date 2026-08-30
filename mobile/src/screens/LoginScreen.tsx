import { useState } from 'react';
import { ActivityIndicator, StyleSheet, Text, TextInput, TouchableOpacity, View } from 'react-native';
import { api } from '../api';
import { theme } from '../theme';

export function LoginScreen({
  serverUrl,
  onSignedIn,
  onChangeServer,
}: {
  serverUrl: string;
  onSignedIn: () => void;
  onChangeServer: () => void;
}) {
  const [userName, setUserName] = useState('');
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit() {
    setBusy(true);
    setError(null);
    try {
      await api.login(userName.trim(), password);
      onSignedIn();
    } catch {
      // The server returns the same 401 for unknown user, deactivated user
      // and wrong password. Being specific here would undo that.
      setError('Sign-in failed. Check your username and password.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <View style={styles.container}>
      <Text style={styles.title}>Sign in</Text>
      <Text style={styles.subtitle}>{serverUrl}</Text>

      <TextInput
        style={styles.input}
        value={userName}
        onChangeText={setUserName}
        placeholder="Username"
        placeholderTextColor={theme.muted}
        autoCapitalize="none"
        autoCorrect={false}
        editable={!busy}
      />

      <TextInput
        style={styles.input}
        value={password}
        onChangeText={setPassword}
        placeholder="Password"
        placeholderTextColor={theme.muted}
        secureTextEntry
        editable={!busy}
        onSubmitEditing={() => void submit()}
      />

      {error ? <Text style={styles.error}>{error}</Text> : null}

      <TouchableOpacity
        style={[styles.button, busy && styles.buttonDisabled]}
        onPress={() => void submit()}
        disabled={busy}
      >
        {busy ? <ActivityIndicator color="#fff" /> : <Text style={styles.buttonText}>Sign in</Text>}
      </TouchableOpacity>

      <TouchableOpacity style={styles.linkButton} onPress={onChangeServer}>
        <Text style={styles.link}>Change server</Text>
      </TouchableOpacity>
    </View>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, justifyContent: 'center', padding: 24, backgroundColor: theme.bg },
  title: { fontSize: 26, fontWeight: '700', color: theme.text },
  subtitle: { fontSize: 13, color: theme.muted, marginTop: 4, marginBottom: 26 },
  input: {
    borderWidth: 1,
    borderColor: theme.border,
    borderRadius: 8,
    paddingHorizontal: 14,
    paddingVertical: 13,
    fontSize: 16,
    color: theme.text,
    backgroundColor: theme.surface,
    marginBottom: 12,
  },
  error: { color: theme.danger, marginBottom: 8, fontSize: 14 },
  button: {
    backgroundColor: theme.accent,
    borderRadius: 8,
    paddingVertical: 15,
    alignItems: 'center',
    marginTop: 8,
  },
  buttonDisabled: { opacity: 0.6 },
  buttonText: { color: '#fff', fontSize: 16, fontWeight: '600' },
  linkButton: { marginTop: 18, alignItems: 'center' },
  link: { color: theme.accent, fontSize: 15 },
});
