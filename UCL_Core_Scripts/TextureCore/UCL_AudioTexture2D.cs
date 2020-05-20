using UnityEngine;

namespace UCL.Core.TextureLib {
    public class UCL_AudioTexture2D : UCL_Texture2D {
        public Color m_WaveCol = Color.yellow;

        public UCL_AudioTexture2D(Vector2Int _size, TextureFormat _TextureFormat = TextureFormat.ARGB32) 
            : base(_size, _TextureFormat) {
            Init();
        }
        virtual public void SetAudioData(float[] data) {
            int data_len = data.Length;
            int seg = data_len / width;
            int mid = height / 2;
            for(int i = 0; i < width; i++) {
                float val = 0;
                float m_val = 0;
                int end = (i + 1) * seg;
                if(end > data_len) end = data_len;
                for(int j = i * seg; j < end; j++) {
                    if(data[j] > 0) {
                        val += data[j];
                    } else {
                        m_val += data[j];
                    }

                }
                int avg = Mathf.RoundToInt((val * height) / seg);
                int m_avg = Mathf.RoundToInt((m_val * height) / seg);
                int s = mid + m_avg;
                int h = mid + avg;
                for(int j = 0; j < height; j++) {
                    if(j >= s && j <= h) {
                        SetPixel(i, j, m_WaveCol);
                    } else {
                        SetPixel(i, j, Color.black);
                    }
                    //m_PlayTexture.SetPixel(i, j, avg < j ? Color.black : Color.yellow);
                }
            }
        }
    }
}