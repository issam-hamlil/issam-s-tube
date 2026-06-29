import { useState, useEffect } from 'react';
import { StyleSheet, Text, View, TextInput, Button, Image, ActivityIndicator, Alert, FlatList, TouchableOpacity } from 'react-native';
import * as FileSystem from 'expo-file-system/legacy';
import * as MediaLibrary from 'expo-media-library';
import * as Clipboard from 'expo-clipboard';

const API_BASE_URL = 'https://issams-tube-backend-production.up.railway.app';
const API_KEY = 'my-super-secret-key-2017';

export default function App() {
  // ── Video screen state ────────────────────────────────────────
  const [url, setUrl] = useState('');
  const [loading, setLoading] = useState(false);
  const [result, setResult] = useState(null);
  const [downloading, setDownloading] = useState(false);
  const [downloadProgress, setDownloadProgress] = useState(0);
  const [preparing, setPreparing] = useState(false);

  // ── Audio screen state ────────────────────────────────────────
  const [audioUrl, setAudioUrl] = useState('');
  const [audioExtracting, setAudioExtracting] = useState(false);
  const [audioDownloading, setAudioDownloading] = useState(false);
  const [audioProgress, setAudioProgress] = useState(0);

  // ── Navigation ────────────────────────────────────────────────
  const [screen, setScreen] = useState('home'); // 'home' | 'audio' | 'instagram' | 'history'
  const [history, setHistory] = useState([]);

  const loadHistory = async () => {
    try {
      const response = await fetch(`${API_BASE_URL}/history`, {
        headers: API_KEY ? { 'X-Api-Key': API_KEY } : {},
      });
      const data = await response.json();
      setHistory(data);
    } catch (err) {
      Alert.alert('Could not load history', err.message);
    }
  };

  useEffect(() => {
    if (screen === 'history') loadHistory();
  }, [screen]);

  // ── Video handlers ────────────────────────────────────────────
  const handleFetch = async () => {
    setLoading(true);
    setResult(null);
    try {
      const response = await fetch(`${API_BASE_URL}/extract`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...(API_KEY ? { 'X-Api-Key': API_KEY } : {}),
        },
        body: JSON.stringify({ url }),
      });
      const data = await response.json();
      if (!response.ok) {
        Alert.alert('Error', data.message || 'Something went wrong');
        return;
      }
      setResult(data);
    } catch (err) {
      Alert.alert('Network error', err.message);
    } finally {
      setLoading(false);
    }
  };

  const handleDownload = async () => {
    if (!url) return;

    const { status } = await MediaLibrary.requestPermissionsAsync();
    if (status !== 'granted') {
      Alert.alert('Permission needed', 'Allow access to save videos to your gallery.');
      return;
    }

    setPreparing(true);
    setDownloadProgress(0);

    try {
      const prepResponse = await fetch(`${API_BASE_URL}/download`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...(API_KEY ? { 'X-Api-Key': API_KEY } : {}),
        },
        body: JSON.stringify({ url }),
      });
      const prepData = await prepResponse.json();
      if (!prepResponse.ok) {
        Alert.alert('Error', prepData.message || 'Could not prepare the download');
        return;
      }

      setPreparing(false);
      setDownloading(true);

      const sourceUrl = prepData.download_url.startsWith('http')
        ? prepData.download_url
        : `${API_BASE_URL}${prepData.download_url}`;

      const downloadHeaders = API_KEY ? { 'X-Api-Key': API_KEY } : {};
      const extension = prepData.media_type === 'image' ? 'jpg' : 'mp4';
      const fileUri = FileSystem.documentDirectory + `${Date.now()}.${extension}`;

      const downloadResumable = FileSystem.createDownloadResumable(
        sourceUrl,
        fileUri,
        { headers: downloadHeaders },
        (progress) => {
          const pct = progress.totalBytesWritten / progress.totalBytesExpectedToWrite;
          setDownloadProgress(pct);
        }
      );

      const downloadResult = await downloadResumable.downloadAsync();
      const asset = await MediaLibrary.createAssetAsync(downloadResult.uri);
      await MediaLibrary.createAlbumAsync("Issam's Tube", asset, false);

      Alert.alert('Saved', 'Saved to your gallery in the best quality available.');
    } catch (err) {
      Alert.alert('Download failed', err.message);
    } finally {
      setPreparing(false);
      setDownloading(false);
    }
  };

  const handlePasteFromClipboard = async () => {
    const text = await Clipboard.getStringAsync();
    if (text) setUrl(text);
  };

  // ── Audio handler ─────────────────────────────────────────────
  const handleAudioExtract = async () => {
    if (!audioUrl) return;

    const { status } = await MediaLibrary.requestPermissionsAsync();
    if (status !== 'granted') {
      Alert.alert('Permission needed', 'Allow access to save audio to your device.');
      return;
    }

    setAudioExtracting(true);
    setAudioProgress(0);

    try {
      const response = await fetch(`${API_BASE_URL}/audio`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...(API_KEY ? { 'X-Api-Key': API_KEY } : {}),
        },
        body: JSON.stringify({ url: audioUrl }),
      });
      const data = await response.json();
      if (!response.ok) {
        Alert.alert('Error', data.message || 'Could not extract audio.');
        return;
      }

      setAudioExtracting(false);
      setAudioDownloading(true);

      const sourceUrl = data.download_url.startsWith('http')
        ? data.download_url
        : `${API_BASE_URL}${data.download_url}`;

      const fileUri = FileSystem.documentDirectory + `${Date.now()}.mp3`;

      const downloadResumable = FileSystem.createDownloadResumable(
        sourceUrl,
        fileUri,
        { headers: API_KEY ? { 'X-Api-Key': API_KEY } : {} },
        (progress) => {
          const pct = progress.totalBytesWritten / progress.totalBytesExpectedToWrite;
          setAudioProgress(pct);
        }
      );

      const downloadResult = await downloadResumable.downloadAsync();
      const asset = await MediaLibrary.createAssetAsync(downloadResult.uri);
      await MediaLibrary.createAlbumAsync("Issam's Tube", asset, false);

      Alert.alert('Saved', 'MP3 (HD quality) saved to your device.');
    } catch (err) {
      Alert.alert('Extraction failed', err.message);
    } finally {
      setAudioExtracting(false);
      setAudioDownloading(false);
    }
  };

  const handleAudioPaste = async () => {
    const text = await Clipboard.getStringAsync();
    if (text) setAudioUrl(text);
  };

  // ── Render ────────────────────────────────────────────────────
  return (
    <View style={styles.container}>
      {/* Header */}
      <Text style={styles.title}>Issam's Tube</Text>

      {/* Tab bar */}
      <View style={styles.tabBar}>
        <TouchableOpacity
          style={[styles.tab, screen === 'home' && styles.tabActive]}
          onPress={() => setScreen('home')}
        >
          <Text style={[styles.tabText, screen === 'home' && styles.tabTextActive]}>📹 Video</Text>
        </TouchableOpacity>
        <TouchableOpacity
          style={[styles.tab, screen === 'instagram' && styles.tabActive]}
          onPress={() => setScreen('instagram')}
        >
          <Text style={[styles.tabText, screen === 'instagram' && styles.tabTextActive]}>📷 Insta Pics</Text>
        </TouchableOpacity>
        <TouchableOpacity
          style={[styles.tab, screen === 'audio' && styles.tabActive]}
          onPress={() => setScreen('audio')}
        >
          <Text style={[styles.tabText, screen === 'audio' && styles.tabTextActive]}>🎵 MP3 HD</Text>
        </TouchableOpacity>
        <TouchableOpacity
          style={[styles.tab, screen === 'history' && styles.tabActive]}
          onPress={() => setScreen('history')}
        >
          <Text style={[styles.tabText, screen === 'history' && styles.tabTextActive]}>🕐 History</Text>
        </TouchableOpacity>
      </View>

      {/* ── Video screen ── */}
      {screen === 'home' && (
        <>
          <View style={{ marginBottom: 12 }}>
            <Button title="Paste from clipboard" onPress={handlePasteFromClipboard} color="#666" />
          </View>

          <TextInput
            style={styles.input}
            placeholder="Paste video URL here"
            value={url}
            onChangeText={setUrl}
            autoCapitalize="none"
            autoCorrect={false}
          />

          <Button title="Fetch Video" onPress={handleFetch} disabled={!url || loading} />

          {loading && <ActivityIndicator size="large" style={{ marginTop: 20 }} />}

          {result && (
            <View style={styles.resultCard}>
              {result.thumbnail && <Image source={{ uri: result.thumbnail }} style={styles.thumbnail} />}
              <Text style={styles.resultTitle}>{result.title}</Text>
              <Button
                title={
                  preparing
                    ? (result.media_type === 'image' ? 'Preparing photo…' : 'Preparing high-quality video…')
                    : downloading
                      ? `Downloading… ${Math.round(downloadProgress * 100)}%`
                      : (result.media_type === 'image' ? 'Save Photo' : 'Save Video')
                }
                onPress={handleDownload}
                disabled={preparing || downloading}
              />
            </View>
          )}
        </>
      )}

      {/* ── Audio screen ── */}
      {screen === 'audio' && (
        <>
          <View style={styles.audioHeader}>
            <Text style={styles.audioSubtitle}>Extract HD audio from any video link</Text>
            <Text style={styles.audioNote}>YouTube · Instagram · TikTok · X · Facebook</Text>
          </View>

          <View style={{ marginBottom: 12 }}>
            <Button title="Paste from clipboard" onPress={handleAudioPaste} color="#666" />
          </View>

          <TextInput
            style={styles.input}
            placeholder="Paste video URL here"
            value={audioUrl}
            onChangeText={setAudioUrl}
            autoCapitalize="none"
            autoCorrect={false}
          />

          <View style={styles.extractBtn}>
            <Button
              title={
                audioExtracting
                  ? 'Extracting audio… (this may take a minute)'
                  : audioDownloading
                    ? `Downloading MP3… ${Math.round(audioProgress * 100)}%`
                    : '🎵 Extract MP3 HD'
              }
              onPress={handleAudioExtract}
              disabled={!audioUrl || audioExtracting || audioDownloading}
              color="#7c3aed"
            />
          </View>

          {(audioExtracting || audioDownloading) && (
            <View style={styles.audioProgress}>
              <ActivityIndicator size="large" color="#7c3aed" />
              <Text style={styles.audioProgressText}>
                {audioExtracting
                  ? 'Server is downloading and converting to MP3…'
                  : `Saving to your device… ${Math.round(audioProgress * 100)}%`}
              </Text>
            </View>
          )}
        </>
      )}

      {/* ── Instagram Pictures screen ── */}
      {screen === 'instagram' && (
        <>
          <View style={styles.audioHeader}>
            <Text style={styles.audioSubtitle}>Download high-quality Instagram Photos</Text>
            <Text style={styles.audioNote}>Paste any Instagram post or reel link</Text>
          </View>

          <View style={{ marginBottom: 12 }}>
            <Button title="Paste from clipboard" onPress={handlePasteFromClipboard} color="#666" />
          </View>

          <TextInput
            style={styles.input}
            placeholder="Paste Instagram URL here"
            value={url}
            onChangeText={setUrl}
            autoCapitalize="none"
            autoCorrect={false}
          />

          <View style={styles.extractBtn}>
            <Button
              title="Fetch Instagram Picture"
              onPress={handleFetch}
              disabled={!url || loading}
              color="#e1306c"
            />
          </View>

          {loading && <ActivityIndicator size="large" color="#e1306c" style={{ marginTop: 20 }} />}

          {result && (
            <View style={styles.resultCard}>
              {result.thumbnail && <Image source={{ uri: result.thumbnail }} style={styles.thumbnail} />}
              <Text style={styles.resultTitle}>{result.title}</Text>
              <Button
                title={
                  preparing
                    ? 'Preparing photo…'
                    : downloading
                      ? `Downloading… ${Math.round(downloadProgress * 100)}%`
                      : 'Save Photo to Gallery'
                }
                color="#e1306c"
                onPress={handleDownload}
                disabled={preparing || downloading}
              />
            </View>
          )}
        </>
      )}

      {/* ── History screen ── */}
      {screen === 'history' && (
        <FlatList
          data={history}
          keyExtractor={(item) => String(item.id)}
          renderItem={({ item }) => (
            <View style={styles.historyRow}>
              <Text style={styles.historyPlatform}>{platformLabel(item.platform)}</Text>
              <Text style={styles.historyTitle} numberOfLines={1}>{item.title ?? item.url}</Text>
              <Text style={styles.historyStatus}>{item.success ? '✅' : '❌'}</Text>
            </View>
          )}
        />
      )}
    </View>
  );
}

function platformLabel(platform) {
  switch (platform) {
    case 'TikTok': return '🎵 TikTok';
    case 'Instagram': return '📷 Instagram';
    case 'Facebook': return '👍 Facebook';
    case 'X/Twitter': return '✖️ X';
    case 'YouTube': return '▶️ YouTube';
    case 'LinkedIn': return '💼 LinkedIn';
    default: return '❓ Unknown';
  }
}

const styles = StyleSheet.create({
  container: { flex: 1, paddingTop: 60, paddingHorizontal: 20, backgroundColor: '#fff' },
  title: { fontSize: 24, fontWeight: 'bold', marginBottom: 16, textAlign: 'center' },

  // Tab bar
  tabBar: { flexDirection: 'row', marginBottom: 20, borderRadius: 10, overflow: 'hidden', borderWidth: 1, borderColor: '#e5e7eb' },
  tab: { flex: 1, paddingVertical: 10, alignItems: 'center', backgroundColor: '#f9fafb' },
  tabActive: { backgroundColor: '#7c3aed' },
  tabText: { fontSize: 13, fontWeight: '600', color: '#6b7280' },
  tabTextActive: { color: '#fff' },

  // Shared
  input: { borderWidth: 1, borderColor: '#ccc', borderRadius: 8, padding: 12, marginBottom: 12 },

  // Video screen
  resultCard: { marginTop: 24, alignItems: 'center' },
  thumbnail: { width: 200, height: 200, borderRadius: 8, marginBottom: 12 },
  resultTitle: { fontSize: 16, marginBottom: 12, textAlign: 'center' },

  // Audio screen
  audioHeader: { marginBottom: 20, alignItems: 'center' },
  audioSubtitle: { fontSize: 15, fontWeight: '600', color: '#374151', marginBottom: 4 },
  audioNote: { fontSize: 12, color: '#9ca3af' },
  extractBtn: { marginBottom: 8 },
  audioProgress: { marginTop: 24, alignItems: 'center', gap: 12 },
  audioProgressText: { fontSize: 13, color: '#7c3aed', textAlign: 'center' },

  // History
  historyRow: { flexDirection: 'row', alignItems: 'center', paddingVertical: 10, borderBottomWidth: 1, borderBottomColor: '#eee' },
  historyPlatform: { width: 110, fontSize: 13 },
  historyTitle: { flex: 1, fontSize: 13 },
  historyStatus: { width: 24, textAlign: 'right' },
});
