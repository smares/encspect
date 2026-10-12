add_library(libencspot STATIC ID3tag.cpp MediaFile.cpp
Mp3File.cpp Mp3Structs.cpp Common.cpp)
set_target_properties(libencspot PROPERTIES OUTPUT_NAME encspot)
