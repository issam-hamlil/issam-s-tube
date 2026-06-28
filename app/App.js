import { useState, useEffect } from 'react';
import { StyleSheet, Text, View, TextInput, Button, Image, ActivityIndicator, Alert, FlatList } from 'react-native';
//import * as FileSystem from 'expo-file-system';
import * as FileSystem from 'expo-file-system/legacy';
import * as MediaLibrary from 'expo-media-library';
import * as Clipboard from 'expo-clipboard';

const API_BASE_URL = 'issams-tube-backend-production.up.railway.app'; // your computer's LAN IP — see note below
const API_KEY = 'my-super-secret-key-2017'; // fill in once Phase 8's middleware is live

export default function App() {
  const [url, setUrl] = useState('');
  const [loading, setLoading] = useState(false);
  const [result, setResult] = useState(null);
  const [downloading, setDownloading] = useState(false);
  const [downloadProgress, setDownloadProgress] = useState(0);
  const [preparing, setPreparing] = useState(false);
  const [screen, setScreen] = useState('home'); // 'home' | 'history'
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

  return (
    <View style={styles.container}>
      <View style={{ flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center', marginBottom: 20 }}>
        <Text style={styles.title}>Issam's Tube</Text>
        <Button
          title={screen === 'home' ? 'History' : 'Home'}
          onPress={() => setScreen(screen === 'home' ? 'history' : 'home')}
        />
      </View>

      {screen === 'history' ? (
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
      ) : (
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
                    ? 'Preparing high-quality video…'
                    : downloading
                      ? `Downloading… ${Math.round(downloadProgress * 100)}%`
                      : 'Download'
                }
                onPress={handleDownload}
                disabled={preparing || downloading}
              />
            </View>
          )}
        </>
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
  container: { flex: 1, paddingTop: 80, paddingHorizontal: 20, backgroundColor: '#fff' },
  title: { fontSize: 24, fontWeight: 'bold', marginBottom: 20 },
  input: { borderWidth: 1, borderColor: '#ccc', borderRadius: 8, padding: 12, marginBottom: 12 },
  resultCard: { marginTop: 24, alignItems: 'center' },
  thumbnail: { width: 200, height: 200, borderRadius: 8, marginBottom: 12 },
  resultTitle: { fontSize: 16, marginBottom: 12, textAlign: 'center' },
  historyRow: { flexDirection: 'row', alignItems: 'center', paddingVertical: 10, borderBottomWidth: 1, borderBottomColor: '#eee' },
  historyPlatform: { width: 110, fontSize: 13 },
  historyTitle: { flex: 1, fontSize: 13 },
  historyStatus: { width: 24, textAlign: 'right' },
});
